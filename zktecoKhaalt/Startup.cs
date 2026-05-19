using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using zktecoKhaalt.Controllers;

namespace zktecoKhaalt;

public static class Startup
{
	public static WebApplication InitializeApp(string[] args)
	{
		WebApplicationBuilder webApplicationBuilder = WebApplication.CreateBuilder(args);
		ConfigureServices(webApplicationBuilder);
		WebApplication webApplication = webApplicationBuilder.Build();
		Configure(webApplication);
		webApplication.Lifetime.ApplicationStarted.Register(() => afterStart(webApplication.Services));
		return webApplication;
	}

	private static void ConfigureServices(WebApplicationBuilder builder)
	{
		builder.Services.AddControllers();
		builder.Services.Configure<CameraConfig>(builder.Configuration.GetSection("Camera"));
		builder.Services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<CameraConfig>>().Value);
		
		// Odoo ERP API Integration Registration
		builder.Services.Configure<OdooConfig>(builder.Configuration.GetSection("Odoo"));
		builder.Services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<OdooConfig>>().Value);
		builder.Services.AddHttpClient<OdooService>();

		builder.Services.AddSingleton<apiController>();
		builder.Services.AddHostedService<BackgroundWorkerService>();
		builder.Services.AddCors(delegate(CorsOptions p)
		{
			p.AddPolicy("corspolicy", delegate(CorsPolicyBuilder build)
			{
				build.WithOrigins("*").AllowAnyMethod().AllowAnyHeader();
			});
		});
		builder.Services.AddEndpointsApiExplorer();
		builder.Services.AddSwaggerGen();
	}

	private static void Configure(WebApplication app)
	{
		if (app.Environment.IsDevelopment())
		{
			app.UseSwagger();
			app.UseSwaggerUI();
		}
		app.UseCors("corspolicy");
		app.UseAuthorization();
		app.MapControllers();
	}

	public static void afterStart(IServiceProvider services)
	{
		Console.WriteLine("afterstart orloo ");
		apiController apiController2 = services.GetRequiredService<apiController>();
		_ = apiController2.Kholboy();
	}
}
