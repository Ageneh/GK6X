using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using MiniJSON;

namespace GK6X {
	internal class WebGUI {
		public const int Port = 6464;
		private static readonly WebServer server = new WebServer(Port);
		public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);

		private const string GET_DEVICE_LIST = "GetDeviceList";
		private const string CHANGE_MODE = "ChangeMode";
		private const string GET_PROFILE_LIST = "GetProfileList";
		private const string READ_PROFILE = "ReadProfile";
		private const string WRITE_PROFILE = "WriteProfile";
		private const string DELETE_PROFILE = "DeleteProfile";
		private const string APPLY_CONFIG = "ApplyConfig";
		private const string READ_FILE = "ReadFile";
		private const string WRITE_FILE = "WriteFile";
		private const string READ_LE = "ReadLE";
		private const string WRITE_LE = "WriteLE";
		private const string DELETE_LE = "DeleteLE";
		private const string READ_MACROFILE = "ReadMacrofile";
		private const string WRITE_MACROFILE = "WriteMacrofile";
		private const string DELETE_MACROFILE = "DeleteMacrofile";

		public static DateTime LastPing => server.LastPing;

		public static string UserDataPath => server.UserDataPath;

		private static bool IsUnix() {
			var p = (int) Environment.OSVersion.Platform;
			return p == 4 || p == 6 || p == 128;
		}

		public static void Run() {
			var url = "http://localhost:" + Port;
			if (!server.IsRunning) {
				server.Start();
				if (server.IsRunning) Program.Log("Started web GUI server at " + url);
			}

			if (server.IsRunning) {
				if (IsUnix())
					try {
						Process.Start("open " + url);
					}
					catch { }
				else
					try {
						Process.Start(url);
						//Process.Start("chrome", "--incognito " + url);
					}
					catch { }
			}
		}

		public static void Stop() {
			server.Stop();
		}

		public static void UpdateDeviceList() {
			server.UpdateDeviceList();
		}

		private class WebServer {
			private string dataPath;
			private DateTime lastSessionCleanup;
			private HttpListener listener;
			private readonly TimeSpan sessionCleanupDelay = TimeSpan.FromSeconds(30);
			private readonly TimeSpan sessionPingTimeout = TimeSpan.FromSeconds(10);

			private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
			private Thread thread;

			public WebServer(int port) {
				Port = port;
			}

			public int Port { get; }

			public bool IsRunning => thread != null;

			public string UserDataPath { get; private set; }

			public DateTime LastPing { get; private set; }

			public void UpdateDeviceList() {
				if (!IsRunning) return;
				lock (sessions) {
					foreach (var session in sessions.Values) session.UpdateDeviceList();
				}
			}

			private void LazyCreateAccount(int accountId) {
				var accountDir = Path.Combine(UserDataPath, "Account", accountId.ToString());
				if (!Directory.Exists(accountDir)) {
					Directory.CreateDirectory(accountDir);

					var devicesDir = Path.Combine(accountDir, "Devices");
					var leDir = Path.Combine(accountDir, "LE");
					var macroDir = Path.Combine(accountDir, "Macro");

					Directory.CreateDirectory(devicesDir);
					Directory.CreateDirectory(leDir);
					Directory.CreateDirectory(macroDir);

					foreach (var file in Directory.GetFiles(Path.Combine(dataPath, "res", "data", "macro"), "*.cms"))
						File.Copy(file, Path.Combine(macroDir, Path.GetFileName(file)), true);
					File.Copy(Path.Combine(dataPath, "res", "data", "macro", "macrolist_en.json"),
						Path.Combine(macroDir, "macrolist.json"), true);

					foreach (var file in Directory.GetFiles(Path.Combine(dataPath, "res", "data", "le"), "*.le"))
						File.Copy(file, Path.Combine(leDir, Path.GetFileName(file)), true);
					File.Copy(Path.Combine(dataPath, "res", "data", "le", "lelist_en.json"),
						Path.Combine(leDir, "lelist.json"), true);

					foreach (var dir in Directory.GetDirectories(Path.Combine(dataPath, "device"))) {
						var profilePath = Path.Combine(dir, "data", "profile.json");
						if (File.Exists(profilePath)) {
							var targetDir = Path.Combine(devicesDir, new DirectoryInfo(dir).Name);
							Directory.CreateDirectory(targetDir);

							foreach (var file in Directory.GetFiles(Path.Combine(dir, "data"), "*.json"))
								try {
									var guid = Guid.NewGuid().ToString().ToUpper();
									var json = Json.Deserialize(File.ReadAllText(file)) as Dictionary<string, object>;
									json["GUID"] = guid;
									var str = Json.Serialize(json);
									File.WriteAllBytes(Path.Combine(targetDir, guid + ".cmf"),
										CMFile.Encrypt(Encoding.UTF8.GetBytes(str), CMFileType.Profile));
								}
								catch { }
						}
					}

					// We need to add these model ids as otherwise it fails to pick up models correctly
					var defaultConfig = new Dictionary<string, object>();
					{
						var userInit = new Dictionary<string, object>();
						userInit["LE"] = true;
						userInit["Macro"] = true;
						defaultConfig["UserInit"] = userInit;
					}
					{
						var modelInit = new Dictionary<string, object>();
						foreach (var dir in Directory.GetDirectories(Path.Combine(dataPath, "device"))) {
							var profilePath = Path.Combine(dir, "data", "profile.json");
							if (File.Exists(profilePath)) {
								var modelInfo = new Dictionary<string, object>();
								modelInfo["Macro"] = true;
								modelInfo["LE"] = true;
								modelInfo["Mode"] = 1;
								modelInit[new DirectoryInfo(dir).Name] = modelInfo;
							}
						}

						defaultConfig["ModelInit"] = modelInit;
					}
					File.WriteAllText(Path.Combine(accountDir, "Config.json"), Json.Serialize(defaultConfig));
				}
			}

			// start server!
			public void Start() {
				LastPing = DateTime.Now;
				dataPath = Program.GetDriverDir(Program.BasePath);
				if (string.IsNullOrEmpty(dataPath)) {
					Program.Log("Couldn't find data path");
					return;
				}

				UserDataPath = Path.Combine(dataPath, "UserData");
				if (!Directory.Exists(UserDataPath)) Directory.CreateDirectory(UserDataPath);

				Stop();

				thread = new Thread(delegate() {
					listener = new HttpListener();
					listener.Prefixes.Add("http://localhost:" + Port +
					                      "/"); // localhost only (as we don't have enough sanitization here...)
					listener.Start();
					while (listener != null)
						try {
							// receive request from frontend
							var context = listener.GetContext();
							Program.Log("received request frontend");
							Program.Log("context::" + context.Request);
							Process(context);
						}
						catch { }
				});
				//thread.SetApartmentState(ApartmentState.STA);
				thread.Start();
			}

			public void Stop() {
				if (listener != null) {
					try {
						listener.Stop();
					}
					catch { }

					listener = null;
				}

				if (thread != null) {
					try {
						thread.Abort();
					}
					catch { }

					thread = null;
				}
			}

			private long ExtractModelId(Dictionary<string, object> request) {
				return (long) Convert.ChangeType(request["ModelID"], typeof(long));
			}

			private int ExtractAccountId(Dictionary<string, object> request) {
				return (int) Convert.ChangeType(request["AccoutID"], typeof(int));
			}

			private int ExtractModeIndex(Dictionary<string, object> request) {
				return (int) Convert.ChangeType(request["ModeIndex"], typeof(int));
			}
			
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
			private void Process(HttpListenerContext context) {
				LastPing = DateTime.Now;

				if (DateTime.Now - sessionCleanupDelay > lastSessionCleanup) {
					lastSessionCleanup = DateTime.Now;
					lock (sessions) {
						foreach (var session in new Dictionary<string, Session>(sessions))
							if (DateTime.Now - sessionPingTimeout > session.Value.LastAccess)
								sessions.Remove(session.Key);
					}
				}

				try {
					var url = context.Request.Url.OriginalString;

					byte[] responseBuffer = null;
					var response = string.Empty;
					var contentType = "text/html";

					// This is for requests which don't need a response (used mostly for our crappy index.html error handler...)
					const string successResponse = "OK!";

					// index.html
					if (context.Request.Url.AbsolutePath == "/" ||
					    context.Request.Url.AbsolutePath.ToLower() == "/index.html") {
						var indexFile = Path.Combine(dataPath, "index.html");
						if (File.Exists(indexFile)) {
							var injectedJs = File.ReadAllText(Path.Combine(Program.DataBasePath, "WebGUI.js"));
							injectedJs = injectedJs.Replace("UNIQUE_TOKEN_GOES_HERE", Guid.NewGuid().ToString());

							response = File.ReadAllText(indexFile);
							response = response.Insert(response.IndexOf("<script"),
								"<script>" + injectedJs + "</script>");
						}
					}
					// actions
					else if (context.Request.Url.AbsolutePath.StartsWith("/cms_")) {
						var postData = new StreamReader(context.Request.InputStream).ReadToEnd();
						var json = Json.Deserialize(postData) as Dictionary<string, object>;
						var token = json["token"].ToString();
						Session session;
						if (!sessions.TryGetValue(token, out session)) {
							session = new Session();
							session.Token = token;
							sessions[session.Token] = session;
						}

						session.LastAccess = DateTime.Now;
						// do stuff based on request
						switch (json["requestType"].ToString()) {
							case "ping": {
								lock (session.MessageQueue) {
									var messages = new List<object>();
									while (session.MessageQueue.Count > 0) messages.Add(session.MessageQueue.Dequeue());
									response = Json.Serialize(messages);
								}
							}
								break;
							// call an actual function/action
							case "callFunc": {
								// get function name
								var request = Json.Deserialize((string) json["request"]) as Dictionary<string, object>;
								switch (request["funcname"].ToString()) {
									case GET_DEVICE_LIST: {
										session.UpdateDeviceList();
										response = successResponse;
									}
										break;
									case CHANGE_MODE: {
										/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
										var modelId = ExtractModelId(request);
										var modeIndex = ExtractModeIndex(request);
										var keyboardDevice = KeyboardDeviceManager.GetConnectedDeviceByModelId(modelId);
										if (keyboardDevice != null) keyboardDevice.SetLayer((KeyboardLayer) modeIndex);
										response = successResponse;
									}
										break;
									case GET_PROFILE_LIST: {
										var accountId = ExtractAccountId(request);
										var modelId = ExtractModelId(request);
										LazyCreateAccount(accountId);
										var modelDir = Path.Combine(UserDataPath, "Account", accountId.ToString(),
											"Devices", modelId.ToString());
										if (Directory.Exists(modelDir)) {
											var objs = new List<object>();
											var modelsByIndex = new Dictionary<int, object>();
											foreach (var file in Directory.GetFiles(modelDir, "*.cmf")) {
												var obj = Json.Deserialize(Encoding.UTF8.GetString(CMFile.Load(file)));
												var profile = obj as Dictionary<string, object>;
												modelsByIndex[
													(int) Convert.ChangeType(profile["ModeIndex"], typeof(int))] = obj;
											}

											foreach (var obj in modelsByIndex.OrderBy(x => x.Key)) objs.Add(obj.Value);
											response = Json.Serialize(objs);
										}
									}
										break;
									case READ_PROFILE: {
										var accountId = ExtractAccountId(request);
										var modelId = ExtractModelId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(),
											"Devices", modelId.ToString(), guid + ".cmf");
										if (File.Exists(file)) response = Encoding.UTF8.GetString(CMFile.Load(file));
									}
										break;
									case WRITE_PROFILE: {
										/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
										var accountId = ExtractAccountId(request);
										var modelId = ExtractModelId(request);
										var guid = (string) request["GUID"];
										var data = (string) request["Data"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(),
											"Devices", modelId.ToString(), guid + ".cmf");
										if (File.Exists(file)) {
											///// Write data to File (still dont know where these files are located)
											File.WriteAllBytes(file,
												CMFile.Encrypt(Encoding.UTF8.GetBytes(data),
													CMFileType.Profile)
											);
											response = successResponse;
										}
									}
										break;
									case DELETE_PROFILE: {
										/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
										var accountId = ExtractAccountId(request);
										var modelId = ExtractModelId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(),
											"Devices", modelId.ToString(), guid + ".cmf");
										if (File.Exists(file))
											try {
												File.Delete(file);
												response = successResponse;
											}
											catch { }
									}
										break;
									case APPLY_CONFIG: {
										ApplyConfig(request, response, session, successResponse);
									}
										break;
									case READ_FILE: {
										var type = (int) Convert.ChangeType(request["Type"], typeof(int));
										var path = (string) request["Path"];
										string basePath = null;
										switch (type) {
											case 0: // Data
												switch (path) {
													case "Env.json":
														path = path.ToLower();
														break;
												}

												basePath = dataPath;
												break;
											case 1: // User data
												basePath = UserDataPath;
												if (path.StartsWith("Account/"))
													LazyCreateAccount(int.Parse(path.Split('/')[1]));
												break;
											default:
												Program.Log("Unhandled ReadFile type " + type);
												break;
										}

										if (!string.IsNullOrEmpty(basePath)) {
											var fullPath = Path.Combine(basePath, path);
											//Program.Log("ReadFile: " + fullPath);
											if (File.Exists(fullPath) &&
											    IsFileInDirectoryOrSubDirectory(fullPath, dataPath))
												response = File.ReadAllText(fullPath);
										}
									}
										break;
									case WRITE_FILE: {
										var type = (int) Convert.ChangeType(request["Type"], typeof(int));
										var path = (string) request["Path"];
										var data = (string) request["Data"];
										string basePath = null;
										switch (type) {
											case 0: // Data
												basePath = dataPath;
												break;
											case 1: // User data
												basePath = UserDataPath;
												if (path.StartsWith("Account/"))
													LazyCreateAccount(int.Parse(path.Split('/')[1]));
												break;
											default:
												Program.Log("Unhandled WriteFile type " + type);
												break;
										}

										if (!string.IsNullOrEmpty(basePath)) {
											var fullPath = Path.Combine(basePath, path);
											//Program.Log("WriteFile: " + fullPath);
											if (IsFileInDirectoryOrSubDirectory(fullPath, dataPath)) {
												var dir = Path.GetDirectoryName(fullPath);
												if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
												File.WriteAllText(fullPath, data);
												response = successResponse;
											}
										}
									}
										break;
									case READ_LE: {
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "LE",
											guid + ".le");
										LazyCreateAccount(accountId);
										if (File.Exists(file)) response = Encoding.UTF8.GetString(CMFile.Load(file));
									}
										break;
									case WRITE_LE: {
										/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "LE",
											guid + ".le");
										var data = (string) request["Data"];
										LazyCreateAccount(accountId);
										File.WriteAllBytes(file,
											CMFile.Encrypt(Encoding.UTF8.GetBytes(data), CMFileType.Light));
										response = successResponse;
									}
										break;
									case DELETE_LE: {
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "LE",
											guid + ".le");
										if (File.Exists(file)) {
											File.Delete(file);
											response = successResponse;
										}
									}
										break;
									case READ_MACROFILE: {
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "Macro",
											guid + ".cms");
										if (File.Exists(file)) {
											var macro = new UserDataFile.Macro(null);
											if (macro.LoadFile(file)) {
												var macroJson = new Dictionary<string, object>();
												macroJson["MacroName"] = macro.Name;
												macroJson["GUID"] = macro.Guid;
												var taskList = new List<object>();
												macroJson["TaskList"] = taskList;
												foreach (var action in macro.Actions) {
													var actionJson = new Dictionary<string, object>();
													if (action.Type == MacroKeyType.Key) {
														switch (action.State) {
															case MacroKeyState.Down:
																actionJson["taskName"] = "KeyDown";
																break;
															case MacroKeyState.Up:
																actionJson["taskName"] = "KeyUp";
																break;
														}
													}
													else {
														string button = null;
														switch ((DriverValueMouseButton) action.KeyCode) {
															case DriverValueMouseButton.LButton:
																button = "Left";
																break;
															case DriverValueMouseButton.RButton:
																button = "Right";
																break;
														}

														if (!string.IsNullOrEmpty(button))
															switch (action.State) {
																case MacroKeyState.Down:
																	button += "Down";
																	break;
																case MacroKeyState.Up:
																	button += "Up";
																	break;
															}

														actionJson["taskName"] = button;
													}

													actionJson["taskValue"] = action.ValueStr != null
														? "\"" + action.ValueStr + "\""
														: string.Empty;
													taskList.Add(actionJson);
													if (action.Delay > 0) {
														var delayJson = new Dictionary<string, object>();
														delayJson["taskName"] = "Delay";
														delayJson["taskValue"] = action.Delay;
														taskList.Add(delayJson);
													}
												}

												response = Json.Serialize(macroJson);
											}
										}
									}
										break;
									case WRITE_MACROFILE: {
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "Macro",
											guid + ".cms");
										var data = (string) request["Data"];

										var macroJson = Json.Deserialize(data) as Dictionary<string, object>;

										var macroStr = new StringBuilder();
										macroStr.AppendLine("[General]");
										macroStr.AppendLine("Name=" + macroJson["MacroName"]);
										macroStr.AppendLine("ScriptID=" + guid);
										macroStr.AppendLine("Repeats=1");
										macroStr.AppendLine("StopMode=1");
										macroStr.AppendLine();
										macroStr.AppendLine("[Script]");

										var taskListJson = macroJson["TaskList"] as List<object>;
										foreach (var taskObj in taskListJson) {
											var task = taskObj as Dictionary<string, object>;
											if (!string.IsNullOrEmpty(task["taskValue"].ToString()))
												macroStr.AppendLine((string) task["taskName"] + " " +
												                    task["taskValue"]);
											else
												macroStr.AppendLine((string) task["taskName"]);
										}

										if (!string.IsNullOrEmpty(guid)) {
											File.WriteAllBytes(file,
												CMFile.Encrypt(Encoding.UTF8.GetBytes(macroStr.ToString()),
													CMFileType.Macro));
											response = successResponse;
										}
									}
										break;
									case DELETE_MACROFILE: {
										var accountId = ExtractAccountId(request);
										var guid = (string) request["GUID"];
										LazyCreateAccount(accountId);
										var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "Macro",
											guid + ".cms");
										if (File.Exists(file)) {
											File.Delete(file);
											response = successResponse;
										}
									}
										break;
								}
							}
								break;
						}
					}
					// misc responses
					else {
						// This needs some sanitization...
						var file = Path.Combine(dataPath, context.Request.Url.AbsolutePath.Substring(1));
						if (File.Exists(file) && IsFileInDirectoryOrSubDirectory(file, dataPath)) {
							var extension = Path.GetExtension(file).ToLower();
							switch (extension) {
								case ".js":
									response = File.ReadAllText(file);
									contentType = "application/javascript";
									break;
								case ".png":
									responseBuffer = File.ReadAllBytes(file);
									contentType = "image/png";
									break;
								case ".jpg":
								case ".jpeg":
									responseBuffer = File.ReadAllBytes(file);
									contentType = "image/jpeg";
									break;
								case ".json":
									responseBuffer = File.ReadAllBytes(file);
									contentType = "application/json";
									break;
								case ".cmsl":
								case ".html":
									responseBuffer = File.ReadAllBytes(file);
									contentType = "text/html";
									break;
								case ".css":
									responseBuffer = File.ReadAllBytes(file);
									contentType = "text/css";
									break;
								default:
									Program.Log("Unhandled file type " + extension + " " +
									            context.Request.Url.AbsolutePath);
									break;
							}
						}
					}

					if (responseBuffer == null && response != null) responseBuffer = Encoding.UTF8.GetBytes(response);

					context.Response.ContentType = contentType;
					context.Response.ContentEncoding = Encoding.UTF8;
					context.Response.ContentLength64 = responseBuffer.Length;
					context.Response.KeepAlive = false;
					context.Response.OutputStream.Write(responseBuffer, 0, responseBuffer.Length);
					context.Response.OutputStream.Flush();
					context.Response.StatusCode = (int) HttpStatusCode.OK;
				}
				catch (Exception e) {
					context.Response.KeepAlive = false;
					context.Response.StatusCode = (int) HttpStatusCode.InternalServerError;
					Program.Log("WebGUI exception: " + e);
				}

				context.Response.OutputStream.Close();
			}

			private void ApplyConfig(Dictionary<string, object> request, string response, Session session,
				string successResponse) {
				/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
				var accountId = ExtractAccountId(request);
				var modelId = ExtractModelId(request);
				var guid = (string) request["GUID"];
				LazyCreateAccount(accountId);
				var file = Path.Combine(UserDataPath, "Account", accountId.ToString(), "Devices", modelId.ToString(),
					guid + ".cmf");
				if (File.Exists(file)) {
					var config = Encoding.UTF8.GetString(CMFile.Load(file));
					var data = Json.Deserialize(config) as Dictionary<string, object>;
					var modelIndex = (int) Convert.ChangeType(data["ModeIndex"], typeof(int));
					var layer = (KeyboardLayer) modelIndex;

					foreach (var device in KeyboardDeviceManager.GetConnectedDevices())
						if (device.State.ModelId == modelId) {
							var macrosById = new Dictionary<int, UserDataFile.Macro>();
							var macrosUserDataFile = new UserDataFile();

							//////////////////////////////////////////
							// Keys
							//////////////////////////////////////////
							for (var i = 0; i < 2; i++) {
								var setStr = i == 0 ? "KeySet" : "FnKeySet";
								if (data.ContainsKey(setStr)) {
									var driverValues = new uint[device.State.MaxLogicCode];
									for (var j = 0; j < driverValues.Length; j++)
										driverValues[j] = KeyValues.UnusedKeyValue;
									var keys = data[setStr] as List<object>;
									foreach (var keyObj in keys) {
										var key = keyObj as Dictionary<string, object>;
										var keyIndex = (int) Convert.ChangeType(key["Index"], typeof(int));
										var driverValue = KeyValues.UnusedKeyValue;
										var driverValueStr = (string) key["DriverValue"];
										if (driverValueStr.StartsWith("0x")) {
											if (uint.TryParse(driverValueStr.Substring(2), NumberStyles.HexNumber, null,
												out driverValue)) {
												if (KeyValues.GetKeyType(driverValue) == DriverValueType.Macro &&
												    key.ContainsKey("Task")) {
													var task = key["Task"] as Dictionary<string, object>;
													if (task != null && (string) task["Type"] == "Macro") {
														var taskData = task["Data"] as Dictionary<string, object>;
														var macroGuid = (string) taskData["GUID"];
														var macroFile = Path.Combine(UserDataPath, "Account",
															accountId.ToString(), "Macro", macroGuid + ".cms");
														if (File.Exists(macroFile)) {
															var macro = new UserDataFile.Macro(null);
															macro.LoadFile(macroFile);
															macro.RepeatCount =
																(byte) Convert.ChangeType(taskData["Repeats"],
																	typeof(byte));
															macro.RepeatType =
																(MacroRepeatType) (byte) Convert.ChangeType(
																	taskData["StopMode"], typeof(byte));
															macro.Id = KeyValues.GetKeyData2(driverValue);
															macrosById[macro.Id] = macro;
															macrosUserDataFile.Macros[macroGuid] = macro;
														}
													}
												}
											}
											else {
												driverValue = KeyValues.UnusedKeyValue;
											}
										}

										if (keyIndex >= 0 && keyIndex < driverValues.Length) {
											if (device.State.KeysByLogicCode.ContainsKey(keyIndex))
												Debug.WriteLine(device.State.KeysByLogicCode[keyIndex].KeyName + " = " +
												                (DriverValue) driverValue);
											driverValues[keyIndex] = driverValue;
										}
									}

									device.SetKeys(layer, driverValues, i == 1);
								}
							}

							//////////////////////////////////////////
							// Lighting
							//////////////////////////////////////////
							var userDataFile = new UserDataFile();
							var effects = new Dictionary<string, UserDataFile.LightingEffect>();
							string[] leHeaders = {"ModeLE", "DriverLE"};
							foreach (var leHeader in leHeaders)
								if (data.ContainsKey(leHeader)) {
									var leEntries = data[leHeader] as List<object>;
									if (leEntries == null) {
										// There's only one ModeLE
										leEntries = new List<object>();
										leEntries.Add(data[leHeader]);
									}

									foreach (var entry in leEntries) {
										var modeLE = entry as Dictionary<string, object>;
										var leGuid = (string) modeLE["GUID"];
										var filePath = Path.Combine(UserDataPath, "Account", accountId.ToString(), "LE",
											leGuid + ".le");
										if (!effects.ContainsKey(leGuid) && File.Exists(filePath)) {
											var le = new UserDataFile.LightingEffect(userDataFile, null);
											le.Load(device.State, Encoding.UTF8.GetString(CMFile.Load(filePath)));
											le.Layers.Add(layer);
											userDataFile.LightingEffects[leGuid] = le;
											effects[leGuid] = le;
										}
									}
								}

							device.SetLighting(layer, userDataFile);

							//////////////////////////////////////////
							// Macros
							//////////////////////////////////////////
							device.SetMacros(layer, macrosUserDataFile);

							device.SetLayer(layer);
						}

					session.Enqueue("onApplyResult", "{\"result\":1}");
					response = successResponse;
				}
				else {
					session.Enqueue("onApplyResult", "{\"result\":0}");
				}
			}

			private bool IsFileInDirectoryOrSubDirectory(string filePath, string directory) {
				return IsSameOrSubDirectory(directory, Path.GetDirectoryName(filePath));
			}

			private bool IsSameOrSubDirectory(string basePath, string path) {
				string subDirectory;
				return IsSameOrSubDirectory(basePath, path, out subDirectory);
			}

			private bool IsSameOrSubDirectory(string basePath, string path, out string subDirectory) {
				var di = new DirectoryInfo(Path.GetFullPath(path).TrimEnd('\\', '/'));
				var diBase = new DirectoryInfo(Path.GetFullPath(basePath).TrimEnd('\\', '/'));

				subDirectory = null;
				while (di != null)
					if (di.FullName.Equals(diBase.FullName, StringComparison.OrdinalIgnoreCase)) {
						return true;
					}
					else {
						if (string.IsNullOrEmpty(subDirectory))
							subDirectory = di.Name;
						else
							subDirectory = Path.Combine(di.Name, subDirectory);
						di = di.Parent;
					}

				return false;
			}

			private class Session {
				public DateTime LastAccess;

				public readonly Queue<Dictionary<string, object>>
					MessageQueue = new Queue<Dictionary<string, object>>();

				public string Token;

				public void Enqueue(string functionName, string data) {
					lock (MessageQueue) {
						var message = new Dictionary<string, object>();
						message["funcName"] = functionName;
						message["data"] = data;
						MessageQueue.Enqueue(message);
					}
				}

				public void UpdateDeviceList() {
					var modelInfos = new List<object>();
					foreach (var device in KeyboardDeviceManager.GetConnectedDevices()) {
						var modelInfo = new Dictionary<string, object>();
						modelInfo["ModelID"] = device.State.ModelId;
						modelInfos.Add(modelInfo);
					}

					Enqueue("onDeviceListChanged", Json.Serialize(modelInfos));
				}
			}
		}
	}
}