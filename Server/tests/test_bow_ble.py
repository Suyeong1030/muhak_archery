from app.bow_ble import BOW_BUTTON_UUID, BOW_SERVICE_UUID, BowBleReceiver


def test_bow_ble_contract() -> None:
    async def on_press() -> None:
        return None

    receiver = BowBleReceiver("ArtemisBow", on_press)
    assert receiver.device_name == "ArtemisBow"
    assert BOW_SERVICE_UUID.startswith("7b1e0001")
    assert BOW_BUTTON_UUID.startswith("7b1e0002")
