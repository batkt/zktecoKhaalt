using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace zktecoKhaalt;

public class DeviceData
{
	public static int BUFFERSIZE = 10485760;

	public static byte[] buffer = new byte[BUFFERSIZE];

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

	public static IntPtr RefreshRemoveUser(string ipaddress, int eventType)
	{
		IntPtr intPtr = DeviceKholbolt(ipaddress);
		string text = "Pin";
		string options = "";
		string filter = "EventType=" + eventType;
		string tablename = "transaction";
		int deviceData = GetDeviceData(intPtr, ref buffer[0], BUFFERSIZE, tablename, text, filter, options);
		if (deviceData != 0)
		{
			Console.WriteLine("GetDeviceData ---->" + deviceData);
			string text2 = Encoding.Default.GetString(buffer);
			string[] array = text2.Replace(text, "").Split("\n").Distinct()
				.ToArray();
			if (array != null && array.Length != 0)
			{
				array[^1] = " ";
				string[] array2 = array;
				foreach (string text3 in array2)
				{
					string text4 = text3.Replace(" ", "").Trim();
					if (text4 != "")
					{
						Console.WriteLine("strcount ---->" + text4);
						tablename = "transaction";
						filter = "Pin=" + text4;
						deviceData = DeleteDeviceData(intPtr, tablename, filter, options);
						tablename = "userauthorize";
						deviceData = DeleteDeviceData(intPtr, tablename, filter, options);
						tablename = "user";
						deviceData = DeleteDeviceData(intPtr, tablename, filter, options);
					}
				}
			}
			return intPtr;
		}
		Console.WriteLine("GetDeviceData Emplty!" + deviceData);
		return PullLastError();
	}

	public static IntPtr DeviceKholbolt(string ipaddress)
	{
		Console.WriteLine("Kholbolt ekhlekh");
		Console.WriteLine("---------------->>" + ipaddress);
		IntPtr intPtr = Connect("protocol=TCP,ipaddress=" + ipaddress + ",port=4370,timeout=2000,passwd=");
		Console.WriteLine((IntPtr.Zero != intPtr) ? "Kholbolt amjilttai" : "Connect device failed!");
		return (IntPtr.Zero != intPtr) ? intPtr : PullLastError();
	}

	public static IntPtr UserKhadgalakh(string ipAddress, string barCodes)
	{
		IntPtr intPtr = DeviceKholbolt(ipAddress);
		Console.WriteLine("UserKhadgalakh --- >> Connect ---" + intPtr);
		int num = SetDeviceParam(intPtr, "DeviceID=4370,Door1SensorType=2,Door1Drivertime=6,Door1Intertime=3");
		if (num >= 0)
		{
			Console.WriteLine("SetDeviceParam --->" + num);
			string text = "Pin";
			string options = "";
			string filter = "";
			string tablename = "user";
			num = GetDeviceData(intPtr, ref buffer[0], BUFFERSIZE, tablename, text, filter, options);
			if (num >= 0)
			{
				Console.WriteLine("GetDeviceData ---->" + num);
				if (num > 0)
				{
					int num2 = 0;
					string text2 = Encoding.Default.GetString(buffer);
					string[] array = text2.Replace(text, "").Split("\n").Distinct()
						.ToArray();
					if (array != null && array.Length != 0)
					{
						array[^1] = " ";
						string[] array2 = array;
						foreach (string text3 in array2)
						{
							string text8 = text3.Replace(" ", "").Trim();
							if (text8 != "")
							{
								Console.WriteLine("UserKhadgalakh pin ---->" + text8);
								num2 += int.Parse(text8);
							}
						}
					}
					num = num2;
					Console.WriteLine("UserKhadgalakh pin ---->" + num);
				}
				else
				{
					num++;
				}
				Console.WriteLine("GetDeviceDataCount ---" + num);
				string[] array3 = barCodes.Split(",");
				string text5 = "";
				string text6 = "";
				string text7 = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0, 0).ToString("yyyyMMdd");
				string text4 = "";
				Console.WriteLine("----------------->>" + text7);
				string[] array4 = array3;
				foreach (string text8 in array4)
				{
					text5 = text5 + ((text5 != "") ? "\r\n" : "") + "Pin=" + num + "\tCardNo=" + text8 + "\tGroup=1\tStartTime=" + text7 + "\tEndTime=" + text7;
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
					Console.WriteLine(" timezone ----------------->>" + SetDeviceData(intPtr, tablename, "TimezoneId=1\tSunTime1=00010937\tMonTime1=00010937\tTueTime1=00010937\tWedTime1=00010937\tThuTime1=00010937\tFriTime1=00010937\tSatTime1=00010937", options));
					tablename = "userauthorize";
					Console.WriteLine(" userauthorize ----------------->>" + SetDeviceData(intPtr, tablename, text6, options));
					Console.WriteLine(" userauthorize ----------------->>" + SetDeviceData(intPtr, tablename, text4, options));
					tablename = "multimcard";
					Console.WriteLine(" multimcard ----------------->>" + SetDeviceData(intPtr, tablename, "Index=1\tDoorId=1\tGroup1=1", options));
					Console.WriteLine(" multimcard ----------------->>" + SetDeviceData(intPtr, tablename, "Index=1\tDoorId=2\tGroup1=1", options));
					return intPtr;
				}
				Console.WriteLine("Connect device failed!");
				return PullLastError();
			}
			Console.WriteLine("GetDeviceDataCount Faild --->" + num);
			return PullLastError();
		}
		Console.WriteLine("SetDeviceParam Faild --->" + num);
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
