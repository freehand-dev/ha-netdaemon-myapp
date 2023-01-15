using System;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using HuaweiWS5200.Models;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.MqttEntityManager.Models;
using System.Collections.Generic;
using System.Linq;
using NetDaemon.HassModel.Integration;
using NetDaemon.Client.HomeAssistant.Model;
using System.Net.Http;
using HuaweiWS5200.API;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Reactive.Concurrency;
using System.Threading;
using NetDaemon.HassModel.Entities;
using System.Xml.Linq;
using System.Net.Mail;

namespace HuaweiWS5200
{
    [NetDaemonApp]
    public class HuaweiWS5200App: IAsyncInitializable, IAsyncDisposable
    {
        private readonly ILogger<HuaweiWS5200App> _logger;
        private readonly HuaweiWS5200Client _client;
        private readonly IAppConfig<HuaweiWS5200Config> _config;
        private readonly IScheduler _scheduler;
        private readonly IMqttEntityManager _entityManager;
        private readonly IHaContext _haContext;

        #region 'entities'
        private readonly string _internetConnectionEntityId = "binary_sensor.netdaemon_huawei_router_internet_connection";
        private readonly string _connectedDevicesEntityId = "sensor.netdaemon_huawei_router_connected_devices";
        private readonly string _guestNetworkEntityId = "switch.netdaemon_huawei_router_guest_network";
        private readonly string _infoEntityId = "sensor.netdaemon_huawei_router_info";
        private readonly string _uptimeEntityId = "sensor.netdaemon_huawei_router_uptime";
        private readonly string _publicIpEntityId = "sensor.netdaemon_huawei_router_public_ip";
        #endregion

        public HuaweiWS5200App(IHaContext ha, IScheduler scheduler, IMqttEntityManager entityManager, IAppConfig<HuaweiWS5200Config> config, ILogger<HuaweiWS5200App> logger, HuaweiWS5200Client client)
        {
            this._entityManager = entityManager;
            this._haContext = ha;
            this._scheduler = scheduler;
            this._logger = logger;
            this._client = client;
            this._config = config;
            this._client.BaseAddress = new Uri(this._config.Value.Uri ?? throw new Exception("Uri is null"));
        }

        internal async Task UodateEntitiesStatus()
        {
            // auth
            bool _heartBeat = await _client.HeartBeatAsync();
            if (!_heartBeat)
            {
                this._logger.LogDebug($"HeartBeat={_heartBeat}, try reconnected.");
                var reconnected = await _client.LoginAsync(_config.Value.Username ?? throw new Exception("Username is null"), _config.Value.Password ?? throw new Exception("Passsword is null")).ConfigureAwait(false);
                this._logger.LogDebug($"Reconnected {(reconnected ? "OK" : "FAILED")}");
            }

            //
            try
            {
                var deviceInfo = await _client.GetDeviceInfoAsync();
                if (deviceInfo != null)
                {
                    if (!string.IsNullOrEmpty(deviceInfo.FriendlyName))
                        await _entityManager.SetStateAsync(this._infoEntityId, deviceInfo.FriendlyName);

                    await _entityManager.SetAttributesAsync(this._infoEntityId, new
                    {
                        serial_number = deviceInfo.SerialNumber,
                    });


                    if (deviceInfo.UpTime.HasValue)
                    {
                        var uptimeSeconds = deviceInfo.UpTime.Value;
                        await _entityManager.SetStateAsync(this._uptimeEntityId, TimeSpan.FromSeconds(uptimeSeconds).ToString());
                        await _entityManager.SetAttributesAsync(this._uptimeEntityId, new
                        {
                            seconds = uptimeSeconds,
                        });
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{_client.GetType().Name} GetDeviceInfoAsync");
            }


            //
            try 
            {
                var wanDiagnose = await _client.GetWanDiagnoseAsync();
                if (wanDiagnose != null)
                {
                    if (!string.IsNullOrEmpty(wanDiagnose.ExternalIPAddress))
                        await _entityManager.SetStateAsync(this._publicIpEntityId, wanDiagnose.ExternalIPAddress);

                    if (!string.IsNullOrEmpty(wanDiagnose.Status))
                        await _entityManager.SetStateAsync(this._internetConnectionEntityId, wanDiagnose.Status);

                    await _entityManager.SetAttributesAsync(this._internetConnectionEntityId, new
                    {
                        mac_address = wanDiagnose.MACAddress,
                        external_ip_address = wanDiagnose.ExternalIPAddress,
                        uptime_seconds = wanDiagnose.Uptime,
                        uptime = (wanDiagnose.Uptime != null) ? TimeSpan.FromSeconds(wanDiagnose.Uptime.Value).ToString() : "-",
                    });
                }
            }                
            catch (Exception e)
            {
                _logger.LogError(e, $"{_client.GetType().Name} GetWanDiagnoseAsync");
            }

            //
            try 
            {
                var hostInfo = await _client.GetHostInfoAsync();
                if (hostInfo != null)
                {
                    var allClients = hostInfo.Count();
                    var onlineClients = hostInfo.Where(x => x.Active == true).Count();
                    var offlineClients = hostInfo.Where(x => x.Active == false).Count();
                    var guestClients = hostInfo.Where(x => x.Active == true && x.IsGuest == true).Count();
                    await _entityManager.SetStateAsync(this._connectedDevicesEntityId, Convert.ToString(onlineClients));
                    await _entityManager.SetAttributesAsync(this._connectedDevicesEntityId, new
                    {
                        online_clients = onlineClients,
                        all_clients = allClients,
                        guset_clients = guestClients,
                        offline_clients = offlineClients,
                    });
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{_client.GetType().Name} GetHostInfoAsync");
            }

            //
            try
            {
                var guestNetwork = await _client.GetGuestNetworkAsync();
                if (guestNetwork != null)
                {
                    bool IsGuestNetworkEnabled = !guestNetwork.Any(c => c.EnableFrequency == false);
                    await _entityManager.SetStateAsync(this._guestNetworkEntityId, IsGuestNetworkEnabled ? "On" : "Off").ConfigureAwait(false);
                    await _entityManager.SetAttributesAsync(this._guestNetworkEntityId, new
                    {
                        ssid = guestNetwork.FirstOrDefault()?.WifiSsid,
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{_client.GetType().Name} GetGuestNetworkAsync");
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            //
            await _entityManager.CreateAsync(this._publicIpEntityId,
                    new EntityCreationOptions(Name: "Public IP"), 
                    new
                    {
                        icon = "mdi:ip-outline"
                    })
                   .ConfigureAwait(false);

            // 
            await _entityManager.CreateAsync(this._internetConnectionEntityId,
                    new EntityCreationOptions(Name: "Internet connection", DeviceClass: "connectivity", PayloadOn: "Connected", PayloadOff: "Disconnected"),
                    new
                    {
                        icon = "mdi:web"
                    })
                   .ConfigureAwait(false);

            // 
            await _entityManager.CreateAsync(this._connectedDevicesEntityId,
                    new EntityCreationOptions(Name: "Connected devices"),
                    new
                    {
                        icon = "mdi:account-multiple",
                        unit_of_measurement = "clients"
                    })
                   .ConfigureAwait(false);
            //
            await _entityManager.CreateAsync(this._uptimeEntityId,
                    new EntityCreationOptions(Name: "Router uptime", DeviceClass: "duration"),
                    new
                    {
                        icon = "mdi:timer-outline",
                    })
                   .ConfigureAwait(false);

            //
            await _entityManager.CreateAsync(this._infoEntityId,
                    new EntityCreationOptions(Name: "Router"))
                   .ConfigureAwait(false);

            // 
            await _entityManager.CreateAsync(this._guestNetworkEntityId,
                    new EntityCreationOptions(Name: "Guest WiFi", PayloadOn: "On", PayloadOff: "Off", DeviceClass: "switch"), new
                    {
                        icon = "mdi:wifi"
                    })
                    .ConfigureAwait(false);

            //
            await UodateEntitiesStatus().ConfigureAwait(false);


            // 
            _logger.LogDebug($"PrepareCommandSubscriptionAsync[{this._guestNetworkEntityId}].Subscribe");
            (await _entityManager.PrepareCommandSubscriptionAsync(this._guestNetworkEntityId).ConfigureAwait(false))
              .Subscribe(new Action<string>(async state =>
              {
                  try
                  {
                      this._logger.LogDebug($"PrepareCommandSubscriptionAsync, state={state}");

                      // auth
                      bool _heartBeat = await _client.HeartBeatAsync();
                      if (!_heartBeat)
                      {
                          this._logger.LogDebug($"HeartBeat={_heartBeat}, try reconnected.");
                          var reconnected = await _client.LoginAsync(_config.Value.Username ?? throw new Exception("Username is null"), _config.Value.Password ?? throw new Exception("Passsword is null")).ConfigureAwait(false);
                          this._logger.LogDebug($"Reconnected {(reconnected ? "OK": "FAILED")}");
                      }
                       
                      bool IsGuestNetworkEnabled = await _client.IsGuestNetworkEnabledAsync().ConfigureAwait(false);
                      string newState = IsGuestNetworkEnabled ? "On" : "Off";
                      this._logger.LogDebug($"GuestNetwork is {newState}");
                      switch (state)
                      {
                          case "On":
                              if (!IsGuestNetworkEnabled)
                              {
                                  this._logger.LogInformation($"EnableGuestNetworkAsync");
                                  var _ = await _client.EnableGuestNetworkAsync().ConfigureAwait(false);
                                  newState = _ ? "On" : "Off";
                              }
                              break;
                          case "Off":
                              if (IsGuestNetworkEnabled)
                              {
                                  this._logger.LogInformation($"DisableGuestNetworkAsync");
                                  var _ = await _client.DisableGuestNetworkAsync().ConfigureAwait(false);
                                  newState = _ ? "Off" : "On";
                              }    
                              break;
                      }
                      this._logger.LogDebug($"GuestNetwork is {newState}");
                      await _entityManager.SetStateAsync(this._guestNetworkEntityId, newState).ConfigureAwait(false);

                  } 
                  catch (Exception e)
                  {
                      _logger.LogError(e, $"PrepareCommandSubscriptionAsync[{this._guestNetworkEntityId}]");
                  }
              }));

              // 
              _scheduler.ScheduleCron("0/1 * * * *", async () =>
              {
                  try
                  {
                      _logger.LogDebug("ScheduleCron.action");
                      // 
                      await UodateEntitiesStatus().ConfigureAwait(false);
                  }
                  catch (Exception e)
                  {
                      _logger.LogError(e, "Scheduler error");
                  }
              });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.LogoutAsync();
            } 
            catch { }
        }
    }
}
