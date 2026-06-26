using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace zktecoKhaalt;

public class DeviceData
{
	public static int BUFFERSIZE = 1048576;

	// NOTE: Do NOT use a shared static buffer — each call must allocate its own local buffer
	// to prevent data corruption when RefreshRemoveUser and UserKhadgalakh overlap.

	private static readonly System.Collections.Generic.HashSet<string> _processedKeys = 
		new System.Collections.Generic.HashSet<string>();

	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _lastGateSyncTimes = 
		new System.Collections.Generic.Dictionary<string, DateTime>();

	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _lastConnectFailTimes = 
		new System.Collections.Generic.Dictionary<string, DateTime>();

	private static IntPtr _activeHandle = IntPtr.Zero;
	private static string _connectedIp = "";
	private static readonly SemaphoreSlim _sdkLock = new SemaphoreSlim(1, 1);

	[DllImport("plcommpro.dll")]
	public static extern IntPtr Connect(string Parameters);

	[DllImport("plcommpro.dll")]
	public static extern IntPtr PullLastError();

	[DllImport("plcommpro.dll")]
	public static extern int ControlDevice(IntPtr h, int operationid, int param1, int param2, int param3, int param4, string options);

	[DllImport("plcommpro.dll")]
	public static extern int SetDeviceParam(IntPtr h, string itemvalues);

	[DllImport("plcommpro.dll")]
	public static extern int GetDeviceData(IntPtr h, ref byte buffer, int buffersize, string tablename, string filename, string filter, string options);

	[DllImport("plcommpro.dll")]
	public static extern int GetDeviceDataCount(IntPtr h, string tablename, string filter, string options);

	[DllImport("plcommpro.dll")]
	public static extern int DeleteDeviceData(IntPtr h, string tablename, string data, string options);

	[DllImport("plcommpro.dll")]
	public static extern int SetDeviceData(IntPtr h, string tablename, string data, string options);

	[DllImport("plcommpro.dll")]
	public static extern void Disconnect(IntPtr h);

	public static string GetCardNoByPin(IntPtr h, string pin)
	{
		byte[] localBuffer = new byte[1024];
		string tablename = "user";
		string filename = "CardNo";
		string filter = "Pin=" + pin;
		int result = GetDeviceData(h, ref localBuffer[0], 1024, tablename, filename, filter, "");
		if (result > 0)
		{
			string text = Encoding.Default.GetString(localBuffer).Split('\0')[0];
			string[] lines = text.Replace("CardNo", "").Split('\n');
			foreach (string line in lines)
			{
				string cardNo = line.Trim();
				if (!string.IsNullOrEmpty(cardNo))
				{
					return cardNo;
				}
			}
		}
		return null;
	}

	public static long EncodeTime(DateTime dt)
	{
		long year = dt.Year;
		long mon = dt.Month;
		long day = dt.Day;
		long hour = dt.Hour;
		long min = dt.Minute;
		long sec = dt.Second;
		return ((year - 2000) * 12 * 31 + (mon - 1) * 31 + day - 1) * 86400 + (hour * 60 + min) * 60 + sec;
	}

	/// <summary>
	/// Reverses ZKTeco's EncodeTime. Returns null if the encoded value is invalid.
	/// </summary>
	public static DateTime? DecodeTime(long encoded)
	{
		try
		{
			long totalSec = encoded % 86400;
			long days    = encoded / 86400;
			int  sec  = (int)(totalSec % 60);
			int  min  = (int)((totalSec / 60) % 60);
			int  hour = (int)(totalSec / 3600);
			int  day  = (int)(days % 31) + 1;
			int  mon  = (int)((days / 31) % 12) + 1;
			int  year = (int)(days / (12 * 31)) + 2000;
			return new DateTime(year, mon, day, hour, min, sec);
		}
		catch
		{
			return null;
		}
	}


	public static async Task<IntPtr> RefreshRemoveUser(string ipaddress, OdooService odooService)
	{
		await _sdkLock.WaitAsync();
		IntPtr intPtr = IntPtr.Zero;
		bool hasError = false;
		try
		{
			intPtr = GetActiveConnection(ipaddress);
			if (intPtr == IntPtr.Zero)
			{
				Console.WriteLine($"RefreshRemoveUser: Connection to {ipaddress} failed. Aborting.");
				return IntPtr.Zero;
			}

			// ==================== TWO-WAY AUTOMATIC GATE SYNCHRONIZATION ENGINE ====================
			bool shouldSync = true;
			if (_lastGateSyncTimes.TryGetValue(ipaddress, out DateTime lastSyncTime))
			{
				if ((DateTime.Now - lastSyncTime).TotalSeconds < 30)
				{
					shouldSync = false;
				}
			}

			if (shouldSync)
			{
				_lastGateSyncTimes[ipaddress] = DateTime.Now;
				try
				{
					var localDb = await odooService.GetAllLocalMembershipsAsync();
					var registeredUsers = new System.Collections.Generic.Dictionary<string, string>(); // Pin -> CardNo
					
					// Retrieve currently enrolled users from the physical gate terminal using standard "Pin" query
					byte[] syncBuffer = new byte[BUFFERSIZE]; // local buffer per call
					int userResult = GetDeviceData(intPtr, ref syncBuffer[0], BUFFERSIZE, "user", "Pin", "", "");
					// Console.WriteLine($"[AUTO-SYNC] userResult: {userResult}");
					if (userResult < 0)
					{
						Console.WriteLine($"[AUTO-SYNC] GetDeviceData(user) failed: {userResult}.");
						hasError = true;
						return IntPtr.Zero;
					}
					if (userResult > 0)
					{
						string userText = Encoding.Default.GetString(syncBuffer).Split('\0')[0];
						// Console.WriteLine($"[AUTO-SYNC] raw users:\n{userText}");
						string[] userLines = userText.Replace("Pin=", "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
						foreach (string line in userLines)
						{
							string pin = line.Trim();
							if (!string.IsNullOrEmpty(pin) && pin != "0")
							{
								string cardNo = GetCardNoByPin(intPtr, pin);
								if (!string.IsNullOrEmpty(cardNo))
								{
									registeredUsers[pin] = cardNo;
								}
							}
						}
					}

					// 1. Sync local JSON to hardware: Add/enroll any active card numbers not currently on the gate
					foreach (var kvp in localDb)
					{
						string cardNo = kvp.Key;
						double points = kvp.Value;
						
						bool shouldRegister = (points > 0 || points == -1.0);

						if (shouldRegister && !registeredUsers.Values.Contains(cardNo))
						{
							// Resolve a new unique numeric PIN for this user
							int newPin = 1000;
							if (registeredUsers.Keys.Count > 0)
							{
								var numericPins = registeredUsers.Keys
									.Select(k => int.TryParse(k, out int p) ? p : 0)
									.Where(p => p > 0)
									.ToList();
								if (numericPins.Count > 0)
								{
									newPin = numericPins.Max() + 1;
								}
							}
							
							string text7 = DateTime.Now.ToString("yyyyMMdd");
							string textFuture = DateTime.Now.AddYears(10).ToString("yyyyMMdd");
							string userStr = $"Pin={newPin}\tCardNo={cardNo}\tGroup=1\tStartTime={text7}\tEndTime={textFuture}";
							string auth1Str = $"Pin={newPin}\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1";
							string auth2Str = $"Pin={newPin}\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2";
							
							int rUser = SetDeviceData(intPtr, "user", userStr, "");
							int rTz = SetDeviceData(intPtr, "timezone", "TimezoneId=1\tSunTime1=00002359\tMonTime1=00002359\tTueTime1=00002359\tWedTime1=00002359\tThuTime1=00002359\tFriTime1=00002359\tSatTime1=00002359", "");
							int rAuth1 = SetDeviceData(intPtr, "userauthorize", auth1Str, "");
							int rAuth2 = SetDeviceData(intPtr, "userauthorize", auth2Str, "");
							int rMulti1 = SetDeviceData(intPtr, "multimcard", "Index=1\tDoorId=1\tGroup1=1", "");
							int rMulti2 = SetDeviceData(intPtr, "multimcard", "Index=1\tDoorId=2\tGroup1=1", "");
							
							Console.WriteLine($"[AUTO-SYNC] Registered new card {cardNo} (PIN {newPin}) onto ZKTeco gate terminal. SetDeviceData results -> User: {rUser}, Tz: {rTz}, Auth1: {rAuth1}, Auth2: {rAuth2}, Multi1: {rMulti1}, Multi2: {rMulti2}");
							if (rUser < 0 || rTz < 0 || rAuth1 < 0 || rAuth2 < 0)
							{
								Console.WriteLine($"[AUTO-SYNC] SetDeviceData failed during enrollment. Marking handle as error.");
								hasError = true;
							}
							else
							{
								registeredUsers[newPin.ToString()] = cardNo; // Add to in-memory list for dynamic consistency
							}
						}
					}

					// 2. Sync depleted cards to hardware: Revoke/delete any users with 0 points or deleted from local DB
					foreach (var kvp in registeredUsers.ToList())
					{
						string pin = kvp.Key;
						string cardNo = kvp.Value;
						
						if (localDb.TryGetValue(cardNo, out double points))
						{
							if (points == 0)
							{
								DeleteDeviceData(intPtr, "userauthorize", "Pin=" + pin, "");
								DeleteDeviceData(intPtr, "user", "Pin=" + pin, "");
								Console.WriteLine($"[AUTO-SYNC] Automatically revoked card {cardNo} (PIN {pin}) from ZKTeco gate terminal.");
							}
						}
						else
						{
							// If the card is registered on the device but not present in our local DB, it has been deleted/depleted! Clean it up!
							DeleteDeviceData(intPtr, "userauthorize", "Pin=" + pin, "");
							DeleteDeviceData(intPtr, "user", "Pin=" + pin, "");
							Console.WriteLine($"[AUTO-SYNC] Automatically cleaned up/deleted card {cardNo} (PIN {pin}) from ZKTeco gate terminal (not found in local database).");
						}
					}
				}
				catch (Exception syncEx)
				{
					Console.WriteLine("[AUTO-SYNC] Error during synchronization: " + syncEx.Message);
					hasError = true;
				}
			}
			// =======================================================================================

			string text = "Pin\tCardno\tTime_second";
			string options = "";
			string filter = "";
			string tablename = "transaction";
			byte[] txBuffer = new byte[BUFFERSIZE]; // local buffer per call
			int deviceData = GetDeviceData(intPtr, ref txBuffer[0], BUFFERSIZE, tablename, text, filter, options);
			if (deviceData > 0)
			{
				string text2 = Encoding.Default.GetString(txBuffer).Split('\0')[0];
				string[] lines = text2.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

				if (lines != null && lines.Length > 0)
				{
					// *** CRITICAL: Delete transactions from device FIRST before processing. ***
					// This ensures that if the service crashes or restarts mid-cycle, the same
					// transactions will NOT be re-processed on the next startup (double-open bug).
					int deleteResult = DeleteDeviceData(intPtr, "transaction", "~", "");
					Console.WriteLine($"[AUTO-SYNC] Cleared device transaction log before processing. Result: {deleteResult}");

					DateTime cutoff = DateTime.Now.AddMinutes(-10);

					foreach (string line in lines)
					{
						if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Pin")) continue;

						string[] parts = line.Split(',');
						if (parts.Length >= 3)
						{
							string pin       = parts[0].Trim();
							string cardNo    = parts[1].Trim();
							string timeSecStr = parts[2].Trim();

							if (string.IsNullOrEmpty(pin) || pin == "0" || pin == "1") continue;

							// *** STALE TRANSACTION GUARD ***
							// On service restart, the device still holds old transaction logs.
							// Ignore any transaction older than 10 minutes to prevent double-opens.
							if (long.TryParse(timeSecStr, out long encodedTime))
							{
								DateTime? txTime = DecodeTime(encodedTime);
								if (txTime.HasValue && txTime.Value < cutoff)
								{
									Console.WriteLine($"[AUTO-SYNC] Skipping stale transaction (age > 10 min): PIN={pin}, Card={cardNo}, TxTime={txTime.Value:HH:mm:ss}");
									continue;
								}
							}

							string key = $"{pin}_{cardNo}_{timeSecStr}";
							if (_processedKeys.Contains(key)) continue;

							_processedKeys.Add(key);
							Console.WriteLine($"[AUTO-SYNC] Detected new unique transaction: {key}");
							Console.WriteLine($"\n>>> [REALTIME GATE SCAN] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Gate: {ipaddress} | Card: {cardNo} | PIN: {pin}");

							int remainingEntries = await odooService.GetRemainingEntriesAsync(cardNo);

							if (remainingEntries > 0)
							{
								int newCount = await odooService.DecrementEntryAsync(cardNo, remainingEntries);
								if (newCount >= 0)
								{
									Console.WriteLine($"Successfully deducted entry. New remaining for {cardNo}: {newCount}");

									if (newCount <= 0)
									{
										Console.WriteLine($"Entries depleted for {cardNo}. Revoking gate access.");
										tablename = "userauthorize";
										filter = "Pin=" + pin;
										DeleteDeviceData(intPtr, tablename, filter, options);
										tablename = "user";
										DeleteDeviceData(intPtr, tablename, filter, options);
									}
								}
								else
								{
									Console.WriteLine($"Failed to decrement entries for {cardNo}. Will retry in next cycle.");
								}
							}
							else if (remainingEntries == 0)
							{
								Console.WriteLine($"Card {cardNo} has 0 entries remaining. Revoking gate access.");
								tablename = "userauthorize";
								filter = "Pin=" + pin;
								DeleteDeviceData(intPtr, tablename, filter, options);
								tablename = "user";
								DeleteDeviceData(intPtr, tablename, filter, options);
							}
						}
					}
				}

				return intPtr;
			}
			if (deviceData < 0)
			{
				Console.WriteLine("GetDeviceData Failed!" + deviceData);
				if (deviceData == -106)
				{
					Console.WriteLine("[AUTO-SYNC] Data overflow (-106) detected. Wiping transaction logs on device to recover...");
					int deleteResult = DeleteDeviceData(intPtr, "transaction", "~", "");
					Console.WriteLine($"[AUTO-SYNC] Log wipe result: {deleteResult}");
				}
				hasError = true;
				return PullLastError();
			}
			return intPtr;
		}
		catch (Exception ex)
		{
			Console.WriteLine("[AUTO-SYNC] Unexpected exception in RefreshRemoveUser: " + ex.Message);
			hasError = true;
			return IntPtr.Zero;
		}
		finally
		{
			if (hasError)
			{
				Console.WriteLine("[AUTO-SYNC] Error detected. Disconnecting active handle.");
				if (_activeHandle != IntPtr.Zero)
				{
					Disconnect(_activeHandle);
					_activeHandle = IntPtr.Zero;
				}
			}
			_sdkLock.Release();
		}
	}

	public static IntPtr GetActiveConnection(string ipaddress)
	{
		// If we have a cached handle for this IP, probe it first to confirm the session is alive.
		// ZKTeco devices drop idle SDK sessions silently — reusing a dead handle returns -2.
		if (_activeHandle != IntPtr.Zero && _connectedIp == ipaddress)
		{
			int probe = GetDeviceDataCount(_activeHandle, "user", "", "");
			if (probe >= 0)
			{
				// Handle is alive — reuse it
				return _activeHandle;
			}
			// Session is dead — clean up and reconnect below
			Console.WriteLine($"[GetActiveConnection] Cached handle for {ipaddress} is stale (probe={probe}). Reconnecting.");
			Disconnect(_activeHandle);
			_activeHandle = IntPtr.Zero;
			_connectedIp = "";
		}

		// Backoff check: If connection failed recently, wait 10 seconds before retrying
		if (_lastConnectFailTimes.TryGetValue(ipaddress, out DateTime lastFailTime))
		{
			if ((DateTime.Now - lastFailTime).TotalSeconds < 10)
			{
				Console.WriteLine($"[GetActiveConnection] Backoff active for {ipaddress}. Skipping reconnect.");
				return IntPtr.Zero;
			}
		}

		if (_activeHandle != IntPtr.Zero)
		{
			Disconnect(_activeHandle);
			_activeHandle = IntPtr.Zero;
		}

		_activeHandle = DeviceKholbolt(ipaddress);
		if (_activeHandle != IntPtr.Zero)
		{
			_connectedIp = ipaddress;
		}
		else
		{
			_lastConnectFailTimes[ipaddress] = DateTime.Now;
		}
		return _activeHandle;
	}

	public static IntPtr DeviceKholbolt(string ipaddress)
	{
		// ZKTeco C3/F-series SDK connect: try multiple comm key variants.
		// Error -2 = handshake rejected, usually due to wrong Comm Key.
		// Common device defaults: empty string, "0", or numeric 0.
		var attempts = new[]
		{
			$"protocol=TCP,ipaddress={ipaddress},port=4370,timeout=4000,passwd=",
			$"protocol=TCP,ipaddress={ipaddress},port=4370,timeout=4000,passwd=0",
			$"protocol=UDP,ipaddress={ipaddress},port=4370,timeout=4000,passwd=",
			$"protocol=UDP,ipaddress={ipaddress},port=4370,timeout=4000,passwd=0",
		};

		foreach (var connStr in attempts)
		{
			IntPtr intPtr = Connect(connStr);
			if (IntPtr.Zero != intPtr)
			{
				Console.WriteLine($"[DeviceKholbolt] Connected to {ipaddress} using: {connStr}");
				return intPtr;
			}
			IntPtr err = PullLastError();
			Console.WriteLine($"[DeviceKholbolt] Attempt failed ({connStr}) — LastError: {err}");
		}

		Console.WriteLine($"[DeviceKholbolt] All connect attempts to {ipaddress} failed.");
		return IntPtr.Zero;
	}

	public static async Task<IntPtr> UserKhadgalakh(string ipAddress, string barCodes)
	{
		await _sdkLock.WaitAsync(); // Use async wait to avoid blocking thread pool threads
		IntPtr intPtr = IntPtr.Zero;
		bool hasError = false;
		try
		{
			intPtr = GetActiveConnection(ipAddress);
			if (intPtr == IntPtr.Zero)
			{
				Console.WriteLine("UserKhadgalakh: Connection failed. Aborting.");
				return IntPtr.Zero;
			}
			Console.WriteLine("UserKhadgalakh --- >> Connect ---" + intPtr);
			string text = "Pin";
			string options = "";
			string filter = "";
			string tablename = "user";
			byte[] localBuffer = new byte[BUFFERSIZE]; // local buffer — never share static buffer
			int num = GetDeviceData(intPtr, ref localBuffer[0], BUFFERSIZE, tablename, text, filter, options);
			if (num >= 0)
			{
				Console.WriteLine("GetDeviceData ---->" + num);
				if (num > 0)
				{
					int maxPin = 1000;
					string text2 = Encoding.Default.GetString(localBuffer).Split('\0')[0];
					string[] array = text2.Replace("Pin=", "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
					if (array != null && array.Length != 0)
					{
						var numericPins = new System.Collections.Generic.List<int>();
						foreach (string pinStr in array)
						{
							string trimmed = pinStr.Replace(" ", "").Trim();
							if (int.TryParse(trimmed, out int parsedPin) && parsedPin > 0)
							{
								numericPins.Add(parsedPin);
							}
						}
						if (numericPins.Count > 0)
						{
							maxPin = numericPins.Max();
						}
					}
					num = maxPin + 1;
					Console.WriteLine("UserKhadgalakh pin ---->" + num);
				}
				else
				{
					num = 1000;
				}
				Console.WriteLine("GetDeviceDataCount ---" + num);
				string[] array3 = barCodes.Split(",");
				string text5 = "";
				string text6 = "";
				string text7 = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0, 0).ToString("yyyyMMdd");
				string textFuture = DateTime.Now.AddYears(10).ToString("yyyyMMdd");
				string text4 = "";
				Console.WriteLine("----------------->>" + text7);
				string[] array4 = array3;
				foreach (string text8 in array4)
				{
					text5 = text5 + ((text5 != "") ? "\r\n" : "") + "Pin=" + num + "\tCardNo=" + text8 + "\tGroup=1\tStartTime=" + text7 + "\tEndTime=" + textFuture;
					text6 = text6 + ((text6 != "") ? "\r\n" : "") + "Pin=" + num + "\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1";
					text4 = text4 + ((text4 != "") ? "\r\n" : "") + "Pin=" + num + "\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2";
					num++;
				}
				Console.WriteLine(" set ----------------->>" + text5);
				num = SetDeviceData(intPtr, tablename, text5, options);
				if (num >= 0)
				{
					Console.WriteLine("The operation is SetDeviceData!");
					tablename = "timezone";
					Console.WriteLine(" timezone ----------------->>" + SetDeviceData(intPtr, tablename, "TimezoneId=1\tSunTime1=00002359\tMonTime1=00002359\tTueTime1=00002359\tWedTime1=00002359\tThuTime1=00002359\tFriTime1=00002359\tSatTime1=00002359", options));
					tablename = "userauthorize";
					Console.WriteLine(" userauthorize ----------------->>" + SetDeviceData(intPtr, tablename, text6, options));
					Console.WriteLine(" userauthorize ----------------->>" + SetDeviceData(intPtr, tablename, text4, options));
					tablename = "multimcard";
					Console.WriteLine(" multimcard ----------------->>" + SetDeviceData(intPtr, tablename, "Index=1\tDoorId=1\tGroup1=1", options));
					Console.WriteLine(" multimcard ----------------->>" + SetDeviceData(intPtr, tablename, "Index=1\tDoorId=2\tGroup1=1", options));
					return intPtr;
				}
				Console.WriteLine("Connect device failed!");
				hasError = true;
				return PullLastError();
			}
			Console.WriteLine("GetDeviceDataCount Faild --->" + num);
			hasError = true;
			return PullLastError();
		}
		catch (Exception ex)
		{
			Console.WriteLine("[UserKhadgalakh] Unexpected exception: " + ex.Message);
			hasError = true;
			return IntPtr.Zero;
		}
		finally
		{
			if (hasError)
			{
				Console.WriteLine("[UserKhadgalakh] Error detected. Disconnecting active handle.");
				if (_activeHandle != IntPtr.Zero)
				{
					Disconnect(_activeHandle);
					_activeHandle = IntPtr.Zero;
				}
			}
			_sdkLock.Release();
		}
	}

	public static async Task ClearUsersAsync(string ipaddress)
	{
		await _sdkLock.WaitAsync();
		IntPtr intPtr = IntPtr.Zero;
		bool hasError = false;
		try
		{
			intPtr = GetActiveConnection(ipaddress);
			if (intPtr != IntPtr.Zero)
			{
				DeleteDeviceData(intPtr, "userauthorize", "~", "");
				DeleteDeviceData(intPtr, "user", "~", "");
				Console.WriteLine($"[CLEANUP] Successfully wiped all users from gate terminal {ipaddress} to clear corrupt PIN memory.");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("[CLEANUP] Error during user wipe: " + ex.Message);
			hasError = true;
		}
		finally
		{
			if (hasError)
			{
				Console.WriteLine("[CLEANUP] Error detected. Disconnecting active handle.");
				if (_activeHandle != IntPtr.Zero)
				{
					Disconnect(_activeHandle);
					_activeHandle = IntPtr.Zero;
				}
			}
			_sdkLock.Release();
		}
	}

	public static void Kholbolt(string ipaddress)
	{
		int num = 0;
		Console.WriteLine("Hello");
		IntPtr intPtr = Connect("protocol=TCP,ipaddress=" + ipaddress + ",port=4370,timeout=2000,passwd=");
		Console.WriteLine("Connect ---" + intPtr);
		if (IntPtr.Zero != intPtr)
		{
			string text = "";
			string options = "";
			string filter = "";
			string tablename = "user";
			text = "DeviceID=4370,Door1SensorType=2,Door1Drivertime=254,Door1Intertime=3";
			Console.WriteLine("GetDeviceData ---" + SetDeviceParam(intPtr, text));
			num = GetDeviceDataCount(intPtr, tablename, filter, options);
			Console.WriteLine("GetDeviceDataCount ---" + num);
			if (num >= 0)
			{
				Console.WriteLine("The operation is successfully!");
				num = SetDeviceData(intPtr, tablename, "Pin=3\tCardNo=4112\tGroup=1\tStartTime=20240830\tEndTime=20240831\r\nPin=1\tCardNo=2362441074\tGroup=1\tStartTime=20240830\tEndTime=20240831\r\nPin=2\tCardNo=2408301901\tGroup=1\tStartTime=20240830\tEndTime=20240831", options);
				if (num >= 0)
				{
					Console.WriteLine("The operation is SetDeviceData!");
				}
				else
				{
					Console.WriteLine("Connect device failed!");
					PullLastError();
				}
				tablename = "timezone";
				num = SetDeviceData(intPtr, tablename, "TimezoneId=1\tSatTime1=00010937\tHol1Time1=00010937", options);
				tablename = "userauthorize";
				num = SetDeviceData(intPtr, tablename, "Pin=1\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1\r\nPin=2\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1\r\nPin=3\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1", options);
				num = SetDeviceData(intPtr, tablename, "Pin=1\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2\r\nPin=2\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2\r\nPin=3\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2", options);
				tablename = "multimcard";
				num = SetDeviceData(intPtr, tablename, "Index=1\tDoorId=1\tGroup1=1", options);
				num = SetDeviceData(intPtr, tablename, "Index=1\tDoorId=2\tGroup1=1", options);
			}
			else
			{
				Console.WriteLine("Connect device failed!");
				PullLastError();
			}
		}
		else
		{
			Console.WriteLine("Connect device failed!");
			PullLastError();
		}
		if (num >= 0)
		{
			Console.WriteLine("The operation is successfully!");
		}
		Console.WriteLine("End ---");
	}
}
