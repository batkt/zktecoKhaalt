using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using zktecoKhaalt.Controllers;

public class BackgroundWorkerService : BackgroundService
{
	private readonly ILogger<BackgroundWorkerService> _logger;
	private readonly apiController _apiController;

	public BackgroundWorkerService(ILogger<BackgroundWorkerService> logger, apiController apiController)
	{
		_logger = logger;
		_apiController = apiController;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				// _logger.LogInformation("Worker running at : {time}", DateTime.Now);
				await _apiController.HeartBeat();
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Heartbeat aldaa");
			}
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(3000.0), stoppingToken);
			}
			catch (TaskCanceledException)
			{
				// Normal on graceful shutdown — exit the loop cleanly
				break;
			}
		}
	}
}
