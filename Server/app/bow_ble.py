import asyncio
from collections.abc import Awaitable, Callable
from typing import Any


BOW_SERVICE_UUID = "7b1e0001-6a5b-4f5c-9d7a-7c9f2e4a1001"
BOW_BUTTON_UUID = "7b1e0002-6a5b-4f5c-9d7a-7c9f2e4a1001"


class BowBleReceiver:
    def __init__(
        self,
        device_name: str,
        on_press: Callable[[], Awaitable[None]],
        *,
        scan_timeout: float = 8.0,
        reconnect_delay: float = 2.0,
    ) -> None:
        self.device_name = device_name
        self.on_press = on_press
        self.scan_timeout = scan_timeout
        self.reconnect_delay = reconnect_delay

    async def run(self) -> None:
        from bleak import BleakClient, BleakScanner

        while True:
            print(f"[Bow BLE] scanning for {self.device_name}...")

            def matches(device: Any, advertisement: Any) -> bool:
                name = advertisement.local_name or device.name or ""
                return name == self.device_name

            device = await BleakScanner.find_device_by_filter(
                matches,
                timeout=self.scan_timeout,
            )
            if device is None:
                print("[Bow BLE] device not found; scanning again")
                await asyncio.sleep(self.reconnect_delay)
                continue

            disconnected = asyncio.Event()
            loop = asyncio.get_running_loop()

            def on_disconnect(_: Any) -> None:
                loop.call_soon_threadsafe(disconnected.set)

            try:
                async with BleakClient(
                    device,
                    disconnected_callback=on_disconnect,
                ) as client:
                    print(f"[Bow BLE] connected: {device.name or self.device_name}")

                    def on_button(_: Any, data: bytearray) -> None:
                        if data and data[0] == 1:
                            print("[Bow BLE] button press received")
                            loop.call_soon_threadsafe(
                                lambda: asyncio.create_task(self.on_press())
                            )

                    await client.start_notify(BOW_BUTTON_UUID, on_button)
                    print("[Bow BLE] ready for button presses")
                    await disconnected.wait()
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                print(f"[Bow BLE] connection error: {exc}")

            print("[Bow BLE] disconnected; reconnecting")
            await asyncio.sleep(self.reconnect_delay)
