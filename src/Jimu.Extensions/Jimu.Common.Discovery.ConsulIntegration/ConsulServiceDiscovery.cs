﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Jimu.Server;

namespace Jimu.Common.Discovery.ConsulIntegration
{
    public class ConsulServiceDiscovery : IServiceDiscovery, IDisposable
    {
        private readonly ConsulClient _consul;
        private readonly ISerializer _serializer;
        private readonly string _serviceCategory;

        private JimuServiceRoute[] _routes;
        public ConsulServiceDiscovery(string ip, int port, string serviceCategory, ISerializer serializer)
        {
            _serializer = serializer;
            _serviceCategory = serviceCategory;
            _consul = new ConsulClient(config =>
            {
                config.Address = new Uri($"http://{ip}:{port}");
            });
        }

        public void Dispose()
        {
            _consul.Dispose();
        }

        public async Task<List<JimuServiceRoute>> GetRoutesAsync()
        {
            if (_routes != null && _routes.Any())
            {
                return _routes.ToList();
            }

            if (_consul.KV.Keys(_serviceCategory).Result.Response?.Count() > 0)
            {
                //var result = (await this._consul.KV.List(this._serviceCategory)).Response?.Select(x=>encoding);
                var keys = (await _consul.KV.Keys(_serviceCategory)).Response;
                _routes = await GetRoutes(keys);
            }

            return (_routes ?? (_routes = new JimuServiceRoute[0])).ToList();
        }

        public async Task<List<JimuAddress>> GetAddressAsync()
        {
            var routes = await GetRoutesAsync();
            var addresses = new List<JimuAddress>();
            if (routes != null && routes.Any())
            {
                addresses = routes.SelectMany(x => x.Address).Distinct().ToList();
            }
            return addresses;
        }

        public async Task ClearAsync()
        {
            var queryResult = await _consul.KV.List(_serviceCategory);
            var response = queryResult.Response;
            if (response != null)
            {
                foreach (var result in response)
                {
                    await _consul.KV.DeleteCAS(result);
                }
            }
        }

        public async Task SetRoutesAsync(IEnumerable<JimuServiceRoute> routes)
        {
            var existingRoutes = await GetRoutes(routes.Select(x => GetKey(x.ServiceDescriptor.Id)));
            var routeDescriptors = new List<JimuServiceRouteDesc>(routes.Count());
            foreach (var route in routes)
            {
                var existingRoute = existingRoutes.FirstOrDefault(x => x.ServiceDescriptor.Id == route.ServiceDescriptor.Id);
                if (existingRoute != null)
                    route.Address = route.Address.Concat(existingRoute.Address.Except(route.Address)).ToList();

                routeDescriptors.Add(new JimuServiceRouteDesc
                {
                    ServiceDescriptor = route.ServiceDescriptor,
                    AddressDescriptors = route.Address?.Select(x => new JimuAddressDesc
                    {
                        Type = $"{x.GetType().FullName}, {x.GetType().Assembly.GetName()}",
                        Value = _serializer.Serialize<string>(x)
                    })
                });
            }

            await SetRoutesAsync(routeDescriptors);
        }

        private async Task SetRoutesAsync(IEnumerable<JimuServiceRouteDesc> routes)
        {

            foreach (var serviceRoute in routes)
            {
                var nodeData = _serializer.Serialize<byte[]>(serviceRoute);
                var keyValuePair = new KVPair(GetKey(serviceRoute.ServiceDescriptor.Id)) { Value = nodeData };
                await _consul.KV.Put(keyValuePair);
            }
        }

        private string GetKey(string id)
        {
            return $"{_serviceCategory}{id}";
        }

        private async Task<JimuServiceRoute[]> GetRoutes(IEnumerable<string> children)
        {
            var routes = new List<JimuServiceRoute>(children.ToArray().Count());
            foreach (var child in children)
            {
                var route = await GetRoute(child);
                if (route != null)
                    routes.Add(route);
            }
            return routes.ToArray();
        }

        private async Task<JimuServiceRoute> GetRoute(string path)
        {
            var queryResult = await _consul.KV.Keys(path);
            if (queryResult.Response == null)
            {
                return null;
            }
            var data = (await _consul.KV.Get(path)).Response?.Value;
            if (data == null)
            {
                return null;
            }
            var descriptor = _serializer.Deserialize<byte[], JimuServiceRouteDesc>(data);

            List<JimuAddress> addresses = new List<JimuAddress>(descriptor.AddressDescriptors.ToArray().Count());
            foreach (var addDesc in descriptor.AddressDescriptors)
            {
                var addrType = Type.GetType(addDesc.Type);
                addresses.Add(_serializer.Deserialize(addDesc.Value, addrType) as JimuAddress);
            }

            return new JimuServiceRoute
            {
                Address = addresses,
                ServiceDescriptor = descriptor.ServiceDescriptor
            };
        }

        /// <summary>
        /// clean specify address service
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public async Task ClearAsync(string address)
        {
            var routes = await GetRoutesAsync();
            var remoteRouteIds = (from route in routes
                                  where route.Address.Count() == 1 && (route.Address.Any(x => x == null || route.Address.First().Code == address))
                                  select route.ServiceDescriptor.Id).ToArray();
            foreach (var remoteRouteId in remoteRouteIds)
            {
                await _consul.KV.Delete(GetKey(remoteRouteId));
            }
        }

        public async Task ClearAsyncByServiceId(string serviceId)
        {
            await _consul.KV.Delete(GetKey(serviceId));
        }
    }
}