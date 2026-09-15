namespace RetailPOS.Application.Services;

using System;

public interface IMaintenanceScopeFactory
{
    IDisposable CreateMaintenanceScope();
}
