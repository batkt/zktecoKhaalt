using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

namespace zktecoKhaalt.Controllers;

[ApiController]
[Route("api")]
public class apiController : ControllerBase
{
	private readonly CameraConfig _cameraConfig;

	public apiController(CameraConfig cameraConfig)
	{
		_cameraConfig = cameraConfig;
	}

	private string GetCameraIp(int index)
	{
		if (_cameraConfig.Ips.Length > index && !string.IsNullOrWhiteSpace(_cameraConfig.Ips[index]))
		{
			return _cameraConfig.Ips[index];
		}
		throw new InvalidOperationException($"Camera IP at index {index} is not configured.");
	}

	[HttpGet("Kholboy")]
	public ActionResult<IEnumerable<string>> Kholboy()
	{
		var logs = new List<string>();
		for (int index = 0; index < _cameraConfig.Ips.Length; index++)
		{
			string ip = _cameraConfig.Ips[index];
			if (string.IsNullOrWhiteSpace(ip))
			{
				continue;
			}

			if (index == 0)
			{
				IntPtr result0 = DeviceData.RefreshRemoveUser(ip, 0);
				logs.Add($"{ip} event 0 => {result0}");
				Console.WriteLine($"{ip} 0 ret --- >> {result0}");
			}

			IntPtr result3 = DeviceData.RefreshRemoveUser(ip, 3);
			logs.Add($"{ip} event 3 => {result3}");
			Console.WriteLine($"{ip} 3 ret --- >> {result3}");
		}

		return Ok(logs);
	}

	public void HeartBeat()
	{
		Kholboy();
	}

	[Route("userKhadgalakh/{barCodes}")]
	public ActionResult<string> userKhadgalakh(string barCodes)
	{
		Console.WriteLine("userKhadgalakh --- >> start");
		foreach (string ip in _cameraConfig.Ips)
		{
			if (string.IsNullOrWhiteSpace(ip))
			{
				continue;
			}

			IntPtr result = DeviceData.UserKhadgalakh(ip, barCodes);
			Console.WriteLine($"userKhadgalakh {ip} --- >> {result}");
		}

		return Ok("Amjilttai");
	}
}
