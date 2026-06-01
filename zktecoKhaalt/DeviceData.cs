using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace zktecoKhaalt;

public class DeviceData
{
	public static int BUFFERSIZE = 10485760;

	public static byte[] buffer = new byte[BUFFERSIZE];

	private static readonly System.Collections.Generic.HashSet<string> _processedKeys = 
		new System.Collections.Generic.HashSet<string>();

	private static bool _isWatermarkInitialized = false;

	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _lastSwipeTimes = 
		new System.Collections.Generic.Dictionary<string, DateTime>();

	private static IntPtr _activeHandle = IntPtr.Zero;
	private static string _connectedIp = "";

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

	public static async Task<IntPtr> RefreshRemoveUser(string ipaddress, OdooService odooService)
	{
		IntPtr intPtr = GetActiveConnection(ipaddress);
		if (intPtr == IntPtr.Zero)
		{
			Console.WriteLine("RefreshRemoveUser: Connection failed. Aborting.");
			return IntPtr.Zero;
		}

		// ==================== TWO-WAY AUTOMATIC GATE SYNCHRONIZATION ENGINE ====================
		try
		{
			var localDb = await odooService.GetAllLocalMembershipsAsync();
			var registeredUsers = new System.Collections.Generic.Dictionary<string, string>(); // Pin -> CardNo
			
			// Retrieve currently enrolled users from the physical gate terminal using standard "Pin" query
			Array.Clear(buffer, 0, buffer.Length);
			int userResult = GetDeviceData(intPtr, ref buffer[0], BUFFERSIZE, "user", "Pin", "", "");
			// Console.WriteLine($"[AUTO-SYNC] userResult: {userResult}");
			if (userResult < 0)
			{
				Console.WriteLine($"[AUTO-SYNC] GetDeviceData(user) failed: {userResult}. Clearing active connection handle.");
				Disconnect(_activeHandle);
				_activeHandle = IntPtr.Zero;
				return IntPtr.Zero;
			}
			if (userResult > 0)
			{
				string userText = Encoding.Default.GetString(buffer).Split('\0')[0];
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
					registeredUsers[newPin.ToString()] = cardNo; // Add to in-memory list for dynamic consistency
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
		}
		// =======================================================================================

		string text = "Pin\tCardno\tTime_second";
		string options = "";
		string filter = "";
		string tablename = "transaction";
		Array.Clear(buffer, 0, buffer.Length);
		int deviceData = GetDeviceData(intPtr, ref buffer[0], BUFFERSIZE, tablename, text, filter, options);
		if (deviceData > 0)
		{
			string text2 = Encoding.Default.GetString(buffer).Split('\0')[0];
			string[] lines = text2.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
			
			// Console.WriteLine($"[AUTO-SYNC] Poll - totalLinesCount: {lines?.Length ?? 0}, _isWatermarkInitialized: {_isWatermarkInitialized}");
			// Console.WriteLine($"[AUTO-SYNC] Raw transactions:\n{text2}");
			
			if (!_isWatermarkInitialized)
			{
				if (lines != null)
				{
					foreach (string line in lines)
					{
						if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Pin")) continue;
						
						string[] parts = line.Split(',');
						if (parts.Length >= 3)
						{
							string pin = parts[0].Trim();
							string cardNo = parts[1].Trim();
							string timeSecStr = parts[2].Trim();
							
							if (string.IsNullOrEmpty(pin) || pin == "0" || pin == "1")
							{
								continue;
							}
							
							string key = $"{pin}_{cardNo}_{timeSecStr}";
							_processedKeys.Add(key);
						}
					}
				}
				_isWatermarkInitialized = true;
				// Console.WriteLine($"[AUTO-SYNC] Watermark initialized with {_processedKeys.Count} active transaction keys.");
				return intPtr;
			}
			
			if (lines != null && lines.Length > 0)
			{
				foreach (string line in lines)
				{
					if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Pin")) continue;
					
					string[] parts = line.Split(',');
					if (parts.Length >= 3)
					{
						string pin = parts[0].Trim();
						string cardNo = parts[1].Trim();
						string timeSecStr = parts[2].Trim();
						
						if (string.IsNullOrEmpty(pin) || pin == "0" || pin == "1")
						{
							continue;
						}
						
						string key = $"{pin}_{cardNo}_{timeSecStr}";
						if (_processedKeys.Contains(key))
						{
							continue;
						}
						
						_processedKeys.Add(key);
						Console.WriteLine($"[AUTO-SYNC] Detected new unique transaction: {key}");

						// Debounce filter: Ignore duplicate swipes of the same card within 5 seconds
						if (_lastSwipeTimes.TryGetValue(cardNo, out DateTime lastTime))
						{
							if ((DateTime.Now - lastTime).TotalSeconds < 5)
							{
								Console.WriteLine($"[DEBOUNCE] Ignored duplicate swipe for card {cardNo} (processed within 5 seconds).");
								continue;
							}
						}
						_lastSwipeTimes[cardNo] = DateTime.Now;

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
			
			Disconnect(intPtr);
			_activeHandle = IntPtr.Zero;
			return intPtr;
		}
		Console.WriteLine("GetDeviceData Emplty!" + deviceData);
		Disconnect(_activeHandle);
		_activeHandle = IntPtr.Zero;
		return (deviceData == 0) ? intPtr : PullLastError();
	}

	public static IntPtr GetActiveConnection(string ipaddress)
	{
		if (_activeHandle != IntPtr.Zero && _connectedIp == ipaddress)
		{
			return _activeHandle;
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
		return _activeHandle;
	}

	public static IntPtr DeviceKholbolt(string ipaddress)
	{
		// Console.WriteLine("Kholbolt ekhlekh");
		// Console.WriteLine("---------------->>" + ipaddress);
		IntPtr intPtr = Connect("protocol=TCP,ipaddress=" + ipaddress + ",port=4370,timeout=5000,passwd=");
		if (IntPtr.Zero != intPtr)
		{
			// Console.WriteLine("Kholbolt amjilttai");
			return intPtr;
		}
		else
		{
			IntPtr err = PullLastError();
			Console.WriteLine("Connect device failed! Last Error: " + err);
			return IntPtr.Zero;
		}
	}

	public static IntPtr UserKhadgalakh(string ipAddress, string barCodes)
	{
		IntPtr intPtr = DeviceKholbolt(ipAddress);
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
		Array.Clear(buffer, 0, buffer.Length);
		int num = GetDeviceData(intPtr, ref buffer[0], BUFFERSIZE, tablename, text, filter, options);
		if (num >= 0)
		{
			Console.WriteLine("GetDeviceData ---->" + num);
			if (num > 0)
			{
				int maxPin = 1000;
				string text2 = Encoding.Default.GetString(buffer).Split('\0')[0];
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
				Disconnect(intPtr);
				return intPtr;
			}
			Console.WriteLine("Connect device failed!");
			Disconnect(intPtr);
			return PullLastError();
		}
		Console.WriteLine("GetDeviceDataCount Faild --->" + num);
		Disconnect(intPtr);
		return PullLastError();
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
