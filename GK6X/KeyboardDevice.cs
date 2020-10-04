using System;
using System.Collections.Generic;
using System.Threading;
using HidSharp;

namespace GK6X {
	public class KeyboardDevice : IDisposable {
		internal HidDevice device;
		private int lastSentMessage;
		private readonly object locker = new object();
		internal HidStream stream;
		public KeyboardState State { get; internal set; }
		public bool IsClosed { get; private set; }

		public void Dispose() {
			IsClosed = true;
			try {
				if (stream != null) stream.Close();
				stream = null;
				device = null;
			}
			catch { }
		}

		public void Close() {
			Dispose();
		}

		internal void StartPingThread() {
			stream.InterruptRequested += Stream_InterruptRequested;
			ThreadPool.QueueUserWorkItem(obj => {
				while (!IsClosed) {
					var tick = Environment.TickCount;
					if (tick < lastSentMessage || tick - 300 > lastSentMessage) {
						SendPing();
						if (tick - 2000 > lastSentMessage) {
							Close();
							break;
						}
					}

					Thread.Sleep(100);
				}
			});
		}

		private void Stream_InterruptRequested(object sender, EventArgs e) { }

		private void SendPing() {
			WritePacketNoResponse(OpCodes.Ping, 0, null);
		}

		public void SetLayer(KeyboardLayer layer) {
			WritePacketNoResponse(OpCodes.SetLayer, (byte) layer, null);
		}

		public void SetLighting(KeyboardLayer layer, UserDataFile userData) {
			var lightingEffects = userData.GetLightingEffects(layer);
			if (lightingEffects.Count > 0 || userData.NoLighting || userData.NoLightingLayers.Contains(layer))
				WritePacketNoResponse(OpCodes.LayerResetDataType, (byte) layer, null,
					(byte) KeyboardLayerDataType.Lighting);
			if (lightingEffects.Count > 0)
				using (var packet = new Packet()) {
					const int maxEffects = UserDataFile.LightingEffect.MaxEffects;
					packet.WriteBytes(new byte[maxEffects * 4 * 4]);
					var dataOffsets = new List<Tuple<int, int, int, int>>();

					for (var i = 0; i < Math.Min(maxEffects, lightingEffects.Count); i++) {
						var lightingEffect = lightingEffects[i];
						if (lightingEffect != null) {
							var data1Offset = packet.Index;
							var data1Count = 0;
							switch (lightingEffect.Type) {
								case LightingEffectType.Static: {
									data1Count = 1;
									packet.WriteUInt16((ushort) lightingEffect.Type);
									packet.WriteUInt16(UserDataFile.LightingEffect.NumStaticLightingBytes);
									var keyColors = new uint[UserDataFile.LightingEffect.NumStaticLightingBytes / 4];
									foreach (var keyColor in lightingEffect.KeyColors)
										keyColors[keyColor.Key] = keyColor.Value;
									foreach (var color in keyColors) packet.WriteUInt32(color);
								}
									break;
								case LightingEffectType.Dynamic: {
									data1Count = lightingEffect.TotalFrames;
									foreach (var frame in lightingEffect.Frames)
										for (var j = 0; j < frame.Count; j++) {
											packet.WriteUInt16((ushort) lightingEffect.Type);
											packet.WriteUInt16(22); // 22 bytes of data (176 bits)
											var byteBits = new byte[22];
											foreach (var keyCode in frame.KeyCodes) {
												var byteIndex = keyCode / 8;
												var bitIndex = keyCode % 8;
												byteBits[byteIndex] |= (byte) (1 << bitIndex);
											}

											packet.WriteBytes(byteBits);
										}
								}
									break;
							}

							var data2Offset = packet.Index;
							var data2Count = 0;
							switch (lightingEffect.Type) {
								case LightingEffectType.Static:
									data2Offset = 0;
									data2Count = 0;
									break;
								case LightingEffectType.Dynamic:
									data2Count = lightingEffect.Params.Count;
									foreach (var param in lightingEffect.Params) {
										packet.WriteByte((byte) param.ColorType);
										packet.WriteByte(
											32); // The size of the param buffer? (total size including self)

										// The keys impacted by the param (as a bit array)
										var byteBits = new byte[22];
										foreach (var keyCode in param.Keys) {
											var byteIndex = keyCode / 8;
											var bitIndex = keyCode % 8;
											byteBits[byteIndex] |= (byte) (1 << bitIndex);
										}

										packet.WriteBytes(byteBits);

										packet.WriteUInt32(param.Color);
										if (param.UseRawValues) {
											packet.WriteUInt16((ushort) param.Val1);
											packet.WriteUInt16((ushort) param.Val2);
										}
										else {
											switch (param.ColorType) {
												case LightingEffectColorType.Breathing:
													packet.WriteUInt16(
														(ushort) (param.Val1 == 0 ? 0 : 100 / param.Val1));
													packet.WriteUInt16((ushort) param.Val2); // "StayCount"
													break;
												default:
													// NOTE: RGB values seem to use 0x26 for the 2nd value? Is this just a random uninitialized variable?
													packet.WriteUInt16(
														(ushort) (param.Val1 == 0 ? 0 : 360 / param.Val1));
													packet.WriteUInt16(0);
													break;
											}
										}
									}

									break;
							}

							dataOffsets.Add(new Tuple<int, int, int, int>(data1Offset, data1Count, data2Offset,
								data2Count));
						}
					}

					var tempIndex = packet.Index;
					packet.Index = 0;
					for (var i = 0; i < maxEffects; i++) {
						var offsets = i < lightingEffects.Count ? dataOffsets[i] : null;
						if (offsets != null) {
							packet.WriteInt32(offsets.Item1);
							packet.WriteInt32(offsets.Item2);
							packet.WriteInt32(offsets.Item3);
							packet.WriteInt32(offsets.Item4);
						}
						else {
							packet.WriteInt32(-1);
							packet.WriteInt32(-1);
							packet.WriteInt32(-1);
							packet.WriteInt32(-1);
						}
					}

					packet.Index = tempIndex;
					WritePacketNoResponse(OpCodes.LayerSetLightValues, (byte) layer, packet);
				}
		}

		public void SetMacros(KeyboardLayer layer, UserDataFile userData) {
			WritePacketNoResponse(OpCodes.LayerResetDataType, (byte) layer, null, (byte) KeyboardLayerDataType.Macros);
			// The following check has been disabled so that the WebGUI can assign macros without having to setup the keys
			/*if (userData.GetNumMacros(layer) == 0)
			{
			    return;
			}*/
			using (var packet = new Packet()) {
				// TODO: This should be improved to only send the macros to the layers that require the macro
				foreach (var macro in userData.Macros.Values) {
					if (macro.Id < 0) continue;
					if (macro.Actions.Count * 2 > byte.MaxValue) {
						Program.Log("Macro '" + macro.Name + "' has too many actions (" + macro.Actions.Count +
						            ", limit is " + byte.MaxValue / 2 + ")");
						continue;
					}

					if (macro.Actions.Count == 0 && macro.Id >= 0) {
						Program.Log("Macro '" + macro.Name + "' doesn't have any actions!");
						continue;
					}

					packet.WriteUInt16(0x55AA); // Macro magic (21930 / 0x55AA / AA 55)

					// Crc to be filled out once all data is written
					var crcIndex = packet.Index;
					packet.WriteUInt16(0);

					var numActionInts = (byte) (macro.Actions.Count * 2 - (macro.UseTrailingDelay ? 0 : 1));
					packet.WriteByte(numActionInts);
					packet.WriteByte((byte) macro.Id);
					packet.WriteByte((byte) macro.RepeatType);
					packet.WriteByte(macro.RepeatCount);

					var crcDataStartIndex = packet.Index;
					var crcDataEndIndex = packet.Index + numActionInts * 4;

					for (var i = 0; i < 63; i++)
						if (i < macro.Actions.Count) {
							var action = macro.Actions[i];
							packet.WriteByte(action.KeyCode);
							packet.WriteByte((byte) action.Modifier);
							packet.WriteByte((byte) action.State);
							packet.WriteByte((byte) action.Type);

							if (i < macro.Actions.Count - 1 || macro.UseTrailingDelay && i == macro.Actions.Count - 1) {
								packet.WriteUInt16(action
									.Delay); //TODO: Investigate! (ushort)(action.Delay == 0 ? 0 : action.Delay / 2));// Delay seems to be doubled?
								packet.WriteByte(0); // Always 0?
								packet.WriteByte(
									3); // Always 3? (1/2 use no delay, 0/4 (and maybe others) seem to never stop pressing the key)
							}
							else {
								packet.WriteInt32(0);
							}
						}
						else {
							packet.WriteInt64(0);
						}

					var buffer = packet.GetBuffer();
					var bytesToCrc = new byte[crcDataEndIndex - crcDataStartIndex];
					Buffer.BlockCopy(buffer, crcDataStartIndex, bytesToCrc, 0, bytesToCrc.Length);
					var crc = Crc16.GetCrc(bytesToCrc);

					// Always 0?
					packet.WriteByte(0);

					var tempIndex = packet.Index;
					packet.Index = crcIndex;
					packet.WriteUInt16(crc);
					packet.Index = tempIndex;
				}

				WritePacketNoResponse(OpCodes.LayerSetMacros, (byte) layer, packet);
			}
		}

		public void SetKeys(KeyboardLayer layer, uint[] values, bool fn) {
			WritePacketNoResponse(OpCodes.LayerResetDataType, (byte) layer, null,
				(byte) (fn ? KeyboardLayerDataType.FnKeySet : KeyboardLayerDataType.KeySet));
			using (var packet = new Packet()) {
				foreach (var driverValue in values) packet.WriteUInt32(driverValue);
				WritePacketNoResponse(fn ? OpCodes.LayerFnSetKeyValues : OpCodes.LayerSetKeyValues, (byte) layer,
					packet);
			}
		}

		public void SetIdentifyDriverMacros() {
			using (var packet = new Packet()) {
				for (byte i = 0; i < byte.MaxValue; i++) packet.WriteUInt32((uint) (0x0A010000 | i));
				WritePacketNoResponse(OpCodes.DriverLayerSetKeyValues, (byte) OpCodes_SetDriverLayerKeyValues.KeySet,
					packet);
			}

			using (var packet = new Packet()) {
				packet.WriteByte(1);
				WritePacketNoResponse(OpCodes.DriverMacro, (byte) OpCodes_DriverMacro.BeginEnd, packet);
			}

			using (var packet = new Packet()) {
				for (byte i = 0; i < byte.MaxValue; i++) packet.WriteByte(0);
				packet.WriteByte(0);
				WritePacketNoResponse(OpCodes.DriverMacro, (byte) OpCodes_DriverMacro.KeyboardState, packet);
			}

			using (var packet = new Packet()) {
				packet.WriteByte(0);
				WritePacketNoResponse(OpCodes.DriverMacro, (byte) OpCodes_DriverMacro.BeginEnd, packet);
			}
		}

		public void WritePacketNoResponse(OpCodes op1, byte op2, Packet packet, byte op3 = 0) {
			using (var response = new Packet())
			using (var result = WritePacket(op1, op2, packet, op3)) { }
		}

		public Packet WritePacket(OpCodes op1, byte op2, Packet packet, byte op3 = 0) {
			lock (locker) {
				var numPackets = 1;
				var offset = 0;
				byte[] completeBuffer = null;
				if (packet != null) {
					completeBuffer = packet.GetWrittenBuffer();
					numPackets = completeBuffer.Length / 0x38 + 1;
				}

				var allowsLongOffset = false;
				var offsetOffset = 2;
				var lengthOffset = 4;
				switch (op1) {
					case OpCodes.DriverLayerSetKeyValues:
					case OpCodes.DriverLayerUpdateRealtimeLighting:
					case OpCodes.LayerSetLightValues:
						offsetOffset = 2;
						lengthOffset = 5;
						allowsLongOffset = true;
						break;
					case OpCodes.LayerSetKeyPressLightingEffect:
					case OpCodes.LayerSetKeyValues:
					case OpCodes.LayerFnSetKeyValues:
					case OpCodes.LayerSetMacros:
						offsetOffset = 2;
						lengthOffset = 4;
						break;
				}

				const int reportHeaderLen = 1;

				try {
					for (var i = 0; i < numPackets; i++) {
						var report = new byte[65];
						report[1] = (byte) op1;
						report[2] = op2;
						if (op3 > 0)
							// Not really an op, used for setting keyboard data buffers
							report[3] = op3;
						if (completeBuffer != null) {
							var numBytesToWrite = Math.Min(0x38, completeBuffer.Length - offset);
							Buffer.BlockCopy(completeBuffer, offset, report, reportHeaderLen + 8, numBytesToWrite);
							if (numPackets > 1) {
								report[reportHeaderLen + offsetOffset + 0] = (byte) offset;
								report[reportHeaderLen + offsetOffset + 1] = (byte) (offset >> 8);
								if (allowsLongOffset)
									report[reportHeaderLen + offsetOffset + 3] = (byte) (offset >> 16);
								report[reportHeaderLen + lengthOffset] = (byte) numBytesToWrite;
							}
						}

						Crc16.InsertCrc(report, reportHeaderLen, 6 + reportHeaderLen);
						stream.Write(report);

						var resultBuffer = GetResponse((byte) op1, op2);
						if (resultBuffer[0] != (byte) op1 /* || resultBuffer[1] != op2*/) return null;
						if (!Crc16.ValidateCrc(resultBuffer)) return null;
						if (i == numPackets - 1) {
							var result = new Packet(true, resultBuffer);
							result.Index = 8;
							lastSentMessage = Environment.TickCount;
							return result;
						}

						offset += 0x38;
					}
				}
				catch {
					return null;
				}

				return null;
			}
		}

		private byte[] GetResponse(byte op1, byte op2) {
			// This is a bit of a hack to consume any packets sent from the keyboard which we didn't request
			// such as changing the keyboard layer manually. TODO: Add a proper packet handler and remove this hack.
			const int reportHeaderLen = 1;
			while (true) {
				var resultBufferWithReportId = new byte[65];
				try {
					stream.Read(resultBufferWithReportId);
				}
				catch {
					return null;
				}

				if (resultBufferWithReportId[0] != 0) return null;
				if (resultBufferWithReportId[1] == op1) {
					var resultBuffer = new byte[64];
					Buffer.BlockCopy(resultBufferWithReportId, 1, resultBuffer, 0, resultBuffer.Length);
					return resultBuffer;
				}

				if (resultBufferWithReportId[1] == (byte) OpCodes.DriverKeyCallback) {
					var callbackId = resultBufferWithReportId[3];
					var callbackKeyDown = resultBufferWithReportId[4] != 0;
					if (callbackKeyDown) {
						KeyboardState.Key key;
						State.KeysByLogicCode.TryGetValue(callbackId, out key);
						Program.Log("Index:" + callbackId + " " +
						            (key == null
							            ? "(null)"
							            : "Name:" + key.KeyName + " " + // Name of the key
							              "Indx:" + key.LogicCode + " " + // 'Indx' for logic code / key index
							              "Loc:" + key.LocationCode + " " + // 'Loc' for location code
							              "D:0x" + key.DriverValue.ToString("X8") + " " + // 'D' for DriverValue
							              //"E:" + (DriverValue)key.DriverValue + " " +// 'E' for the Enum variant of the DriverValue
							              "S:" + key
								              .DriverValueName
						            )); // <--- 'S' for string representation (use this for mapping keys in your UserData file)
					}
				}
				else if (!Crc16.ValidateCrc(resultBufferWithReportId, reportHeaderLen, reportHeaderLen + 6)) {
					return null;
				}
			}
		}
	}
}