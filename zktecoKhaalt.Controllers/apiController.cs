using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace zktecoKhaalt.Controllers;

[ApiController]
[Route("api")]
public class apiController : ControllerBase
{
	private readonly CameraConfig _cameraConfig;
	private readonly OdooService _odooService;

	public apiController(CameraConfig cameraConfig, OdooService odooService)
	{
		_cameraConfig = cameraConfig;
		_odooService = odooService;
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
	public async Task<ActionResult<IEnumerable<string>>> Kholboy()
	{
		var logs = new List<string>();
		for (int index = 0; index < _cameraConfig.Ips.Length; index++)
		{
			string ip = _cameraConfig.Ips[index];
			if (string.IsNullOrWhiteSpace(ip))
			{
				continue;
			}

			IntPtr result = await DeviceData.RefreshRemoveUser(ip, _odooService);
			logs.Add($"{ip} scan => {result}");
			Console.WriteLine($"{ip} scan ret --- >> {result}");
		}

		return Ok(logs);
	}

	[HttpGet("clearUsers")]
	public ActionResult<string> ClearUsers()
	{
		foreach (string ip in _cameraConfig.Ips)
		{
			if (string.IsNullOrWhiteSpace(ip)) continue;
			
			IntPtr intPtr = DeviceData.DeviceKholbolt(ip);
			if (intPtr != IntPtr.Zero)
			{
				DeviceData.DeleteDeviceData(intPtr, "userauthorize", "~", "");
				DeviceData.DeleteDeviceData(intPtr, "user", "~", "");
				DeviceData.Disconnect(intPtr);
				Console.WriteLine($"[CLEANUP] Successfully wiped all users from gate terminal {ip} to clear corrupt PIN memory.");
			}
		}
		return Ok("Gate user memory wiped successfully. The background sync will automatically re-enroll all active cards in 15 seconds!");
	}

	public async Task HeartBeat()
	{
		await Kholboy();
	}

	[HttpPost("userKhadgalakh")]
	public async Task<ActionResult<string>> userKhadgalakh([FromBody] UserKhadgalakhRequest request)
	{
		Console.WriteLine("userKhadgalakh (Body POST) --- >> start");
		if (request == null || request.Barcodes == null)
		{
			return BadRequest("Invalid payload.");
		}
		return await RegisterBarcodesAsync(request.Barcodes);
	}

	[Route("userKhadgalakh/{barCodes}")]
	public async Task<ActionResult<string>> userKhadgalakh(string barCodes)
	{
		Console.WriteLine("userKhadgalakh (Route Param) --- >> start: " + barCodes);
		if (string.IsNullOrWhiteSpace(barCodes))
		{
			return BadRequest("Empty barcodes parameter.");
		}

		string cleaned = barCodes.Trim();
		// URL decode the route parameter
		string decoded = System.Net.WebUtility.UrlDecode(cleaned);

		// Check if the route parameter is an embedded JSON string
		if (decoded.StartsWith("{"))
		{
			try
			{
				var request = JsonSerializer.Deserialize<UserKhadgalakhRequest>(decoded);
				if (request != null && request.Barcodes != null)
				{
					return await RegisterBarcodesAsync(request.Barcodes);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Failed to parse JSON route parameter: " + ex.Message);
				return BadRequest("Invalid JSON format in URL parameter.");
			}
		}

		// Fallback: If not JSON, treat it as a standard comma-separated barcode string
		string[] barcodeArray = decoded.Split(',');
		var list = new List<BarcodeItem>();
		foreach (string barcode in barcodeArray)
		{
			string trimmed = barcode.Trim();
			if (!string.IsNullOrEmpty(trimmed))
			{
				list.Add(new BarcodeItem { Barcode = trimmed, Point = 1 });
			}
		}
		return await RegisterBarcodesAsync(list);
	}

	private async Task<ActionResult<string>> RegisterBarcodesAsync(List<BarcodeItem> barcodes)
	{
		if (barcodes == null || barcodes.Count == 0)
		{
			Console.WriteLine("userKhadgalakh --- >> Empty barcodes list.");
			return BadRequest("Empty barcodes list.");
		}

		var validBarCodesList = new List<string>();

		foreach (var item in barcodes)
		{
			string barcode = item.Barcode?.ToString()?.Trim() ?? "";
			if (string.IsNullOrEmpty(barcode))
			{
				continue;
			}

			// Save the barcode's point allowance locally in our JSON database
			await _odooService.SaveLocalMembershipPointsAsync(barcode, item.Point);
			validBarCodesList.Add(barcode);
		}

		if (validBarCodesList.Count == 0)
		{
			Console.WriteLine("userKhadgalakh --- >> No active cards with points to register on device.");
			return BadRequest("No active cards with points to register.");
		}

		string validBarCodesString = string.Join(",", validBarCodesList);

		foreach (string ip in _cameraConfig.Ips)
		{
			if (string.IsNullOrWhiteSpace(ip))
			{
				continue;
			}

			IntPtr result = DeviceData.UserKhadgalakh(ip, validBarCodesString);
			Console.WriteLine($"userKhadgalakh {ip} --- >> {result}");
		}

		return Ok("Amjilttai");
	}
}

public class UserKhadgalakhRequest
{
	[JsonPropertyName("barcodes")]
	public List<BarcodeItem> Barcodes { get; set; } = new List<BarcodeItem>();
}

public class BarcodeItem
{
	[JsonPropertyName("barcode")]
	public object Barcode { get; set; }

	[JsonPropertyName("point")]
	public double Point { get; set; }
}
