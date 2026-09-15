namespace RetailPOS.Server.BackgroundServices;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Enums;

internal static partial class ScheduledBackupLoggerExtensions
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Scheduled Backup Background Service starting.")]
    public static partial void LogServiceStarting(this ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Scheduled Backup Background Service stopped.")]
    public static partial void LogServiceStopped(this ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Error occurred during scheduled backup evaluation.")]
    public static partial void LogEvaluationError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Triggering Daily Scheduled Backup for date {Date}.")]
    public static partial void LogDailyTrigger(this ILogger logger, DateOnly date);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Daily Scheduled Backup completed successfully.")]
    public static partial void LogDailySuccess(this ILogger logger);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "Failed to create scheduled daily backup.")]
    public static partial void LogDailyFailed(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "Triggering Hourly Scheduled Backup.")]
    public static partial void LogHourlyTrigger(this ILogger logger);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Information, Message = "Hourly Scheduled Backup completed successfully.")]
    public static partial void LogHourlySuccess(this ILogger logger);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Error, Message = "Failed to create scheduled hourly backup.")]
    public static partial void LogHourlyFailed(this ILogger logger, Exception ex);
}

public sealed class ScheduledBackupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScheduledBackupBackgroundService> _logger;
    private DateOnly _lastDailyBackupDate = DateOnly.MinValue;
    private DateTime _lastHourlyBackupUtc = DateTime.MinValue;

    public ScheduledBackupBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ScheduledBackupBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogServiceStarting();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndExecuteSchedulesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogEvaluationError(ex);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogServiceStopped();
    }

    private async Task CheckAndExecuteSchedulesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IBackupSettingsStore>();
        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var storeRepo = scope.ServiceProvider.GetRequiredService<IStoreRepository>();

        var config = await settingsStore.GetSettingsAsync(ct).ConfigureAwait(false);
        var store = await storeRepo.GetStoreAsync(ct).ConfigureAwait(false);
        var timeZoneId = store?.TimeZoneId ?? "India Standard Time";

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            tz = TimeZoneInfo.Utc;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var localDate = DateOnly.FromDateTime(localNow);

        // Daily scheduled backup evaluation
        if (config.DailyBackupEnabled && localNow.TimeOfDay >= config.DailyBackupTime && _lastDailyBackupDate < localDate)
        {
            _logger.LogDailyTrigger(localDate);
            try
            {
                await backupService.CreateBackupAsync(
                    BackupType.ScheduledDaily,
                    "SYSTEM_SCHEDULER",
                    "Daily Scheduled Service",
                    $"Scheduled Daily Backup for {localDate}",
                    ct).ConfigureAwait(false);

                _lastDailyBackupDate = localDate;
                _logger.LogDailySuccess();
            }
            catch (Exception ex)
            {
                _logger.LogDailyFailed(ex);
            }
        }

        // Hourly scheduled backup evaluation
        if (config.HourlyBackupEnabled && config.HourlyIntervalHours > 0)
        {
            var interval = TimeSpan.FromHours(config.HourlyIntervalHours);
            if (DateTime.UtcNow - _lastHourlyBackupUtc >= interval)
            {
                _logger.LogHourlyTrigger();
                try
                {
                    await backupService.CreateBackupAsync(
                        BackupType.ScheduledHourly,
                        "SYSTEM_SCHEDULER",
                        "Hourly Scheduled Service",
                        "Scheduled Hourly Backup",
                        ct).ConfigureAwait(false);

                    _lastHourlyBackupUtc = DateTime.UtcNow;
                    _logger.LogHourlySuccess();
                }
                catch (Exception ex)
                {
                    _logger.LogHourlyFailed(ex);
                }
            }
        }
    }
}
