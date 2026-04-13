#!/usr/bin/env python3
import argparse
import signal
import socket
import sys
from dataclasses import dataclass
from pathlib import Path

from evdev import InputDevice, ecodes, ff, list_devices

NOFFB_FORCE_MAX = 10000
EVDEV_FORCE_MAX = 0x7FFF
def clamp(value: int, lo: int, hi: int) -> int:
    return max(lo, min(hi, value))


def scale_constant(value: int) -> int:
    value = clamp(value, -NOFFB_FORCE_MAX, NOFFB_FORCE_MAX)
    return int((value / NOFFB_FORCE_MAX) * EVDEV_FORCE_MAX)


def scale_condition(value: int) -> int:
    value = clamp(value, 0, NOFFB_FORCE_MAX)
    return int((value / NOFFB_FORCE_MAX) * EVDEV_FORCE_MAX)


def detect_ff_axes(device: InputDevice) -> list[int]:
    caps = device.capabilities(absinfo=False, verbose=False)
    axes = caps.get(ecodes.EV_ABS, [])
    preferred = [ecodes.ABS_X, ecodes.ABS_Y]
    found = [axis for axis in preferred if axis in axes]
    return found or axes[:2]


def list_ffb_devices() -> list[InputDevice]:
    devices: list[InputDevice] = []
    for dev_path in list_devices():
        try:
            device = InputDevice(dev_path)
            caps = device.capabilities(absinfo=False, verbose=False)
            if ecodes.EV_FF in caps:
                devices.append(device)
            else:
                device.close()
        except OSError:
            continue
    return devices


def choose_device_path(explicit_path: str | None) -> str:
    if explicit_path:
        return explicit_path

    devices = list_ffb_devices()
    if not devices:
        raise RuntimeError("No force feedback capable devices found")

    print("Available Force Feedback Devices:", flush=True)
    print("==================================", flush=True)
    for index, device in enumerate(devices, start=1):
        print(f"{index}. {device.name}", flush=True)
        print(f"   Path: {device.path}", flush=True)
        print(flush=True)

    if len(devices) == 1:
        print("Only one device found. Using it automatically.\n", flush=True)
        selected = devices[0]
    elif sys.stdin.isatty():
        while True:
            try:
                selection = int(input(f"Select device (1-{len(devices)}): ").strip())
            except ValueError:
                print("Invalid selection. Please try again.", flush=True)
                continue
            if 1 <= selection <= len(devices):
                selected = devices[selection - 1]
                break
            print("Invalid selection. Please try again.", flush=True)
    else:
        selected = devices[0]
        print(f"Multiple devices found; defaulting to first device: {selected.path}", flush=True)

    selected_path = selected.path
    for device in devices:
        device.close()
    return selected_path


@dataclass(slots=True)
class FFBControlMessage:
    type: str
    values: list[int]

    @classmethod
    def from_csv(cls, csv: str) -> "FFBControlMessage":
        parts = [part.strip() for part in csv.strip().split(",")]
        if not parts or not parts[0]:
            raise ValueError("Empty message")
        values = [int(part) for part in parts[1:] if part]
        return cls(parts[0].lower(), values)


class LinuxForceFeedbackController:
    def __init__(
        self,
        dev_path: str,
        constant_gain: float = 1.0,
        condition_gain: float = 1.0,
        enable_conditions: bool = False,
    ):
        self.device = InputDevice(dev_path)
        self.ffb_axes = detect_ff_axes(self.device)
        if not self.ffb_axes:
            raise RuntimeError("No ABS axes detected on target device")
        self.constant_gain = constant_gain
        self.condition_gain = condition_gain
        self.enable_conditions = enable_conditions
        self.keep_running = True
        self.effect_ids: dict[str, int] = {}
        self.ff_capabilities = set(self.device.capabilities(absinfo=False, verbose=False).get(ecodes.EV_FF, []))
        self.damper_supported = enable_conditions and ecodes.FF_DAMPER in self.ff_capabilities
        self.friction_supported = enable_conditions and ecodes.FF_FRICTION in self.ff_capabilities
        self.state_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.state_target = ("127.0.0.1", 5002)

    def upload_effect(self, key: str, effect: ff.Effect) -> int:
        if key in self.effect_ids:
            effect.id = self.effect_ids[key]
        effect_id = self.device.upload_effect(effect)
        self.effect_ids[key] = effect_id
        return effect_id

    def play_effect(self, effect_id: int, repeat: int = 1) -> None:
        self.device.write(ecodes.EV_FF, effect_id, repeat)
        self.device.syn()

    def erase_effect(self, key: str) -> None:
        effect_id = self.effect_ids.pop(key, None)
        if effect_id is None:
            return
        try:
            self.device.erase_effect(effect_id)
        except OSError:
            pass

    def stop_all_effects(self) -> None:
        for key in list(self.effect_ids):
            self.erase_effect(key)

    def shutdown(self) -> None:
        self.keep_running = False
        self.stop_all_effects()
        self.state_socket.close()

    def send_axis_state(self) -> None:
        axis_names = (
            ("X", ecodes.ABS_X),
            ("Y", ecodes.ABS_Y),
            ("Z", ecodes.ABS_Z),
        )
        payload: list[str] = ["axisstate"]
        for label, axis_code in axis_names:
            try:
                info = self.device.absinfo(axis_code)
            except (OSError, TypeError):
                continue
            if info is None:
                continue
            payload.extend((label, str(info.value)))

        if len(payload) > 1:
            self.state_socket.sendto(",".join(payload).encode("ascii"), self.state_target)

    def _build_constant_effect(
        self,
        axis_number: int,
        magnitude: int,
        direction_x: int,
        direction_y: int,
    ) -> ff.Effect:
        magnitude = clamp(int(scale_constant(magnitude) * self.constant_gain), -EVDEV_FORCE_MAX, EVDEV_FORCE_MAX)
        direction_y = -direction_y
        if axis_number == 1:
            direction = 0x0000
        elif axis_number == 2:
            direction = self._cartesian_to_evdev_direction(direction_x, direction_y)
        elif axis_number == 3:
            direction = self._cartesian_to_evdev_direction(direction_y, direction_x)
        else:
            raise ValueError(f"FFBConstantForce Axis Number Value incorrect: {axis_number},{magnitude},{direction_x},{direction_y}")

        effect_type = ff.EffectType(
            ff_constant_effect=ff.Constant(
                magnitude,
                ff.Envelope(0, 0, 0, 0),
            )
        )
        return ff.Effect(
            ecodes.FF_CONSTANT,
            -1,
            direction,
            ff.Trigger(0, 0),
            ff.Replay(0x7FFF, 0),
            effect_type,
        )

    @staticmethod
    def _cartesian_to_evdev_direction(x: int, y: int) -> int:
        if x == 0 and y == 0:
            return 0
        import math

        angle = math.atan2(x, y)
        return int((angle % (2 * math.pi)) * 0x10000 / (2 * math.pi)) & 0xFFFF

    def _build_condition_effect(self, kind: int, coeff_x: int, coeff_y: int) -> ff.Effect:
        x_coeff = clamp(int(scale_condition(coeff_x) * self.condition_gain), 0, EVDEV_FORCE_MAX)
        y_coeff = clamp(int(scale_condition(coeff_y) * self.condition_gain), 0, EVDEV_FORCE_MAX)
        conditions = [
            ff.Condition(10000, 10000, x_coeff, x_coeff, 0, 0),
        ]
        if len(self.ffb_axes) >= 2:
            conditions.append(ff.Condition(10000, 10000, y_coeff, y_coeff, 0, 0))
        effect_type = ff.EffectType(ff_condition_effect=tuple(conditions))
        return ff.Effect(
            kind,
            -1,
            0,
            ff.Trigger(0, 0),
            ff.Replay(0x7FFF, 0),
            effect_type,
        )

    def ffb_constant_force(self, axis_number: int, magnitude: int, direction_x: int, direction_y: int) -> None:
        effect = self._build_constant_effect(axis_number, magnitude, direction_x, direction_y)
        effect_id = self.upload_effect("constant", effect)
        self.play_effect(effect_id)

    def ffb_damper(self, damper_x: int, damper_y: int) -> None:
        if not self.damper_supported:
            return
        effect = self._build_condition_effect(ecodes.FF_DAMPER, damper_x, damper_y)
        try:
            effect_id = self.upload_effect("damper", effect)
            self.play_effect(effect_id)
        except OSError as exc:
            self.damper_supported = False
            self.erase_effect("damper")
            print(f"Damper unsupported/failed, disabling damper path: {exc}", file=sys.stderr, flush=True)

    def ffb_friction(self, friction_x: int, friction_y: int) -> None:
        if not self.friction_supported:
            return
        effect = self._build_condition_effect(ecodes.FF_FRICTION, friction_x, friction_y)
        try:
            effect_id = self.upload_effect("friction", effect)
            self.play_effect(effect_id)
        except OSError as exc:
            self.friction_supported = False
            self.erase_effect("friction")
            print(f"Friction unsupported/failed, disabling friction path: {exc}", file=sys.stderr, flush=True)

    def ffb_autocenter(self, enabled: bool) -> None:
        self.erase_effect("constant")
        self.erase_effect("damper")
        value = 0xFFFF if enabled else 0
        self.device.write(ecodes.EV_FF, ecodes.FF_AUTOCENTER, value)
        self.device.syn()

    def process_message(self, raw_message: str) -> None:
        message = FFBControlMessage.from_csv(raw_message)
        self.send_axis_state()
        print(raw_message.strip(), flush=True)
        if message.type == "autocenter":
            if len(message.values) < 1:
                raise ValueError("autocenter requires 1 value")
            self.ffb_autocenter(bool(message.values[0]))
        elif message.type == "constantforce":
            if len(message.values) < 4:
                raise ValueError("constantforce requires 4 values")
            self.ffb_constant_force(*message.values[:4])
        elif message.type == "constantforce2":
            if len(message.values) < 8:
                raise ValueError("constantforce2 requires 8 values")
            self.ffb_constant_force(*message.values[:4])
            if self.enable_conditions:
                self.ffb_damper(message.values[4], message.values[5])
                self.ffb_friction(message.values[6], message.values[7])
        elif message.type == "damper":
            if len(message.values) < 1:
                raise ValueError("damper requires 1 value")
            if self.enable_conditions:
                self.ffb_damper(message.values[0], message.values[0])
        elif message.type == "friction":
            if len(message.values) < 1:
                raise ValueError("friction requires 1 value")
            # Windows NOFFBController currently ignores top-level friction packets in Program.cs.
            return
        else:
            print(f"Unhandled message type: {message.type}", file=sys.stderr, flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Linux NOFFB controller with message parity to NOFFBController")
    parser.add_argument(
        "--device",
        default=None,
        help="evdev node for the force-feedback device",
    )
    parser.add_argument("--port", type=int, default=5001, help="UDP port to listen on")
    parser.add_argument("--constant-gain", type=float, default=1.0, help="Extra multiplier for constant force magnitude")
    parser.add_argument("--condition-gain", type=float, default=1.0, help="Extra multiplier for damper/friction coefficients")
    parser.add_argument(
        "--enable-conditions",
        action="store_true",
        help="Enable damper/friction handling for constantforce2 packets. Disabled by default because some Linux FFB drivers suppress constant force when condition effects are active.",
    )
    args = parser.parse_args()

    try:
        selected_device = choose_device_path(args.device)
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    dev_path = Path(selected_device)
    if not dev_path.exists():
        print(f"Device not found: {dev_path}", file=sys.stderr)
        return 1

    controller = LinuxForceFeedbackController(
        str(dev_path),
        constant_gain=args.constant_gain,
        condition_gain=args.condition_gain,
        enable_conditions=args.enable_conditions,
    )
    print("Force Feedback Controller Application", flush=True)
    print("======================================", flush=True)
    print(f"Using {controller.device.path} ({controller.device.name})", flush=True)
    print(f"Detected FFB axes: {controller.ffb_axes}", flush=True)
    print(
        f"Conditions enabled: {controller.enable_conditions} | support: damper={controller.damper_supported}, friction={controller.friction_supported}",
        flush=True,
    )
    print(
        f"Gains: constant={controller.constant_gain:.2f}, condition={controller.condition_gain:.2f}",
        flush=True,
    )
    print(f"Listening on UDP port {args.port} (Ctrl+C to quit)", flush=True)

    def shutdown(*_args) -> None:
        controller.shutdown()
        raise SystemExit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", args.port))

    try:
        while controller.keep_running:
            data, _addr = sock.recvfrom(65535)
            try:
                controller.process_message(data.decode("utf-8", errors="replace"))
            except Exception as exc:
                print(str(exc), file=sys.stderr, flush=True)
    finally:
        sock.close()
        controller.shutdown()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
