using HuaweiWS5200.API;
using HuaweiWS5200.Models;
using HuaweiWS5200;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DNSHosting.Models;
using dnshosting.api;
using dnshosting.api.Extensions;
using NetDaemon.Client.HomeAssistant.Model;
using NetDaemon.HassModel.Entities;

namespace DNSHosting
{
    [NetDaemonApp]
    public class DNSHostingApp : IAsyncInitializable
    {
        private readonly ILogger<DNSHostingApp> _logger;
        private readonly DnsHostingClient _client;
        private readonly IAppConfig<DNSHostingConfig> _config;
        private readonly IScheduler _scheduler;
        private readonly IMqttEntityManager _entityManager;
        private readonly IHaContext _haContext;

        public DNSHostingApp(IHaContext ha, IScheduler scheduler, IMqttEntityManager entityManager, IAppConfig<DNSHostingConfig> config, ILogger<DNSHostingApp> logger, DnsHostingClient client)
        {
            this._entityManager = entityManager;
            this._haContext = ha;
            this._scheduler = scheduler;
            this._logger = logger;
            this._client = client;
            this._config = config;

            // 
            foreach (var item in this._config.Value.Domains)
            {
                foreach (var map in item.Maps)
                {
                    _logger.LogDebug($"[{map.Entity?.EntityId}].StateChanges.Subscribe");
                    map.Entity?.StateChanges()
                    .Subscribe(async s =>
                    {
                        try
                        {
                            this._logger.LogDebug($"[{map.Entity.EntityId}].StateChanges: old={s.Old?.State}, new={s.New?.State}");
                            if (!string.IsNullOrEmpty(s.New?.State))
                            {
                                string domain = item.Domain ?? throw new Exception("Domain is null");
                                await _client.LoginAsync(this._config.Value.Username ?? throw new Exception("Username is null"), this._config.Value.Password ?? throw new Exception("Password is null"));
                                try
                                {
                                    foreach (var subdomain in map.Subdomains)
                                    {
                                        this._logger.LogDebug($"AddOrUpdateRecord: {subdomain}.{domain} A {s.New?.State}");
                                    }
                                    await _client.AddOrUpdateRecord(domain, "A", map.Subdomains, s.New.State);
                                }
                                finally
                                {
                                    await _client.LogoutAsync();
                                }
                            }      
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Subscribe[{map.Entity.EntityId}]");
                        }
                    });
                }
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {

            await _client.LoginAsync(this._config.Value.Username ?? throw new Exception("Username is null"), this._config.Value.Password ?? throw new Exception("Password is null"));
            try
            {
                foreach (var item in this._config.Value.Domains)
                {
                    string domain = item.Domain ?? throw new Exception("Domain is null");
                    foreach (var map in item.Maps)
                    {
                        string? state = map.Entity?.State;
                        if (!string.IsNullOrWhiteSpace(state))
                        {
                            foreach (var subdomain in map.Subdomains)
                            {
                                this._logger.LogDebug($"AddOrUpdateRecord: {subdomain}.{domain} A {state}"); 
                            }
                            await _client.AddOrUpdateRecord(domain, "A", map.Subdomains, state);
                        }

                    }
                }
            }
            finally
            {
                await _client.LogoutAsync();
            }
        }
    }

}
