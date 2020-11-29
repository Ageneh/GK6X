﻿using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace GK6X {
	public delegate void KeyboardDeviceConnected(KeyboardDevice device);

	public delegate void KeyboardDeviceDisconnected(KeyboardDevice device);

	public static class KeyboardDeviceManager {
		// vendorid, productids
		internal static Dictionary<ushort, ushort[]> knownProducts = new Dictionary<ushort, ushort[]> {
			{
				7847, // (0x1EA7) SEMITEK INTERNATIONAL (HK) HOLDING LTD.
				new ushort[] {
					2311 // (0x907) GK6X
				}
			}
		};

        /// <summary>
        ///     Device path (string) -> KeyboardDevice
        /// </summary>
        private static readonly Dictionary<string, KeyboardDevice> connectedDevices =
			new Dictionary<string, KeyboardDevice>();

		private static readonly HashSet<string> ignoredDevices = new HashSet<string>();

		private static bool isListening;
		public static event KeyboardDeviceConnected Connected;
		public static event KeyboardDeviceConnected Disconnected;

		public static void StartListener() {
			lock (connectedDevices) {
				if (!isListening) {
					RefreshConnectedDevices();
					DeviceList.Local.Changed += Local_Changed;
					isListening = true;
				}
			}
		}

		public static void StopListener() {
			lock (connectedDevices) {
				if (isListening) {
					DeviceList.Local.Changed -= Local_Changed;
					// Create a copy of the connected devices collection so that we can remove the devices as we iterate
					foreach (var device in connectedDevices.Values.ToList()) device.Close();
					isListening = false;
				}
			}
		}

		private static void Local_Changed(object sender, DeviceListChangedEventArgs e) {
			RefreshConnectedDevices();
		}

		public static KeyboardDevice[] GetConnectedDevices() {
			lock (connectedDevices) {
				return connectedDevices.Values.ToArray();
			}
		}

		public static KeyboardDevice GetConnectedDeviceByModelId(long modelId) {
			lock (connectedDevices) {
				return GetConnectedDevices().FirstOrDefault(device => device.State.ModelId == modelId);
			}
		}

		// for what is the handshake being used for?
		public static void RefreshConnectedDevices() {
			lock (connectedDevices) {
				foreach (var device in DeviceList.Local.GetHidDevices()) {
					if (connectedDevices.ContainsKey(device.DevicePath) ||
					    ignoredDevices.Contains(device.DevicePath)) continue;

					var validDevice = false;
					try {
						// I *think* 65 is used by all GK6X keyboards
						const int reportLength = 65;
						ushort[] productIds;
						if (device.GetMaxInputReportLength() == reportLength &&
						    device.GetMaxOutputReportLength() == reportLength &&
						    knownProducts.TryGetValue((ushort) device.VendorID, out productIds) &&
						    productIds.Contains((ushort) device.ProductID))
							validDevice = true;
					}
					catch {
						ignoredDevices.Add(device.DevicePath);
					}

					if (validDevice) {
						HidStream stream;
						if (device.TryOpen(out stream)) {
							if (!stream.CanWrite) {
								stream.Close();
								continue;
							}

							// for what is the handshake being used for?
							var keyboardState = Handshake(stream);
							if (keyboardState != null) {
								var keyboardDevice = new KeyboardDevice();
								keyboardDevice.State = keyboardState;
								keyboardDevice.stream = stream;
								keyboardDevice.device = device;
								connectedDevices[device.DevicePath] = keyboardDevice;
								stream.Closed += (sender, e) => {
									keyboardDevice.Close();
									lock (connectedDevices) {
										connectedDevices.Remove(device.DevicePath);
									}

									if (Disconnected != null) Disconnected(keyboardDevice);
								};
								if (Connected != null) Connected(keyboardDevice);
								keyboardDevice.StartPingThread();
							}
							else {
								Console.WriteLine("Keyboard handshake failed");
								stream.Close();
							}
						}
					}
				}
			}
		}

		private static KeyboardState Handshake(HidStream stream) {
			try {
				byte bufferSizeA;
				byte bufferSizeB;
				uint firmwareId;
				byte firmwareMinorVersion;
				byte firmwareMajorVersion;
				uint modelId;
				using (var packet = WriteSimplePacket(stream, 0x0901)) {
					if (packet == null) {
						LogHandshakeFailed(stream.Device, "opcode 01 09");
						return null;
					}

					bufferSizeA = packet.ReadByte();
					bufferSizeB = packet.ReadByte();
					if (bufferSizeA == 0 || bufferSizeB == 0) {
						LogHandshakeFailed(stream.Device, "Bad buffer size");
						return null;
					}
				}

				using (var packet = WriteSimplePacket(stream, 0x0101)) {
					if (packet == null) {
						LogHandshakeFailed(stream.Device, "opcode 01 01");
						return null;
					}

					firmwareId = packet.ReadUInt32();
					firmwareMinorVersion = packet.ReadByte();
					firmwareMajorVersion = packet.ReadByte();
					if (firmwareId == 0 || firmwareMinorVersion == 0 && firmwareMajorVersion == 0) {
						LogHandshakeFailed(stream.Device, "Bad firmware id");
						return null;
					}
				}

				using (var packet = WriteSimplePacket(stream, 0x0801)) {
					if (packet == null) {
						LogHandshakeFailed(stream.Device, "opcode 01 08");
						return null;
					}

					var crcBytes = packet.ReadBytes(6);
					var calculatedModelIdCrc = Crc16.GetCrc(crcBytes);
					packet.Index -= 6;
					modelId = packet.ReadUInt32();
					if (modelId == 0) {
						LogHandshakeFailed(stream.Device, "Bad keyboard model id");
						return null;
					}

					var crcValidation1 = packet.ReadUInt16();
					var modelIdCrc = packet.ReadUInt16();
					if (calculatedModelIdCrc != modelIdCrc) {
						LogHandshakeFailed(stream.Device, "Bad keyboard model crc");
						return null;
					}
				}
				// OpCodes_Info.Unk_02 should probably also be sent? Not sure what it's used for though...

				var result = KeyboardState.GetKeyboardState(modelId);
				if (result == null || result.FirmwareId != firmwareId) {
					LogHandshakeFailed(stream.Device, "Couldn't find data for keyboard");
					return null;
				}

				result.FirmwareMinorVersion = firmwareMinorVersion;
				result.FirmwareMajorVersion = firmwareMajorVersion;
				result.InitializeBuffers(bufferSizeA, bufferSizeB);
				return result;
			}
			catch (Exception e) {
				LogHandshakeFailed(stream.Device, "Exception occured. " + e);
				return null;
			}
		}

		private static Packet WriteSimplePacket(HidStream stream, ushort opcode) {
			using (var packet = new Packet()) {
				packet.WriteByte(0); // report id
				packet.WriteUInt16(opcode);
				packet.WriteBytes(new byte[62]);
				var buffer = packet.GetWrittenBuffer();
				Crc16.InsertCrc(buffer, 1, 7); // offset for the report id byte
				stream.Write(buffer);

				var resultBufferWithReportId = new byte[65];
				stream.Read(resultBufferWithReportId);
				if (resultBufferWithReportId[0] != 0) return null;
				var resultBuffer = new byte[64];
				Buffer.BlockCopy(resultBufferWithReportId, 1, resultBuffer, 0, resultBuffer.Length);
				// All handshake packets should have a result code of 1
				if (resultBuffer[2] != 1 ||
				    resultBuffer[0] != (byte) opcode || resultBuffer[1] != opcode >> 8)
					return null;
				if (!Crc16.ValidateCrc(resultBuffer)) return null;
				var result = new Packet(true, resultBuffer);
				result.Index = 8;
				return result;
			}
		}

		private static void LogHandshakeFailed(HidDevice device, string str) {
			Log("Handshake failed (" + device.GetFriendlyName() + ") " + str);
		}

		internal static void Log(string str) {
			Program.Log(str);
		}
	}
}