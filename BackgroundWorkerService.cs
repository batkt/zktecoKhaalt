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
			await Task.Delay(TimeSpan.FromSeconds(59.0), stoppingToken);
			try
			{
				_logger.LogInformation("Worker running at : {time}", DateTime.Now);
				_apiController.HeartBeat();
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Heartbeat aldaa");
			}
			await Task.Delay(10000, stoppingToken);
		}
	}
}
