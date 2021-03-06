﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using ServiceDisc.Models;
using ServiceDisc.Networking;
using ServiceDisc.Networking.HostnameResolution;
using ServiceDisc.Networking.QueueService;
using ServiceDisc.Networking.ServiceDiscConnection;
using ServiceDisc.Networking.WebApi;

namespace ServiceDisc
{
    /// <summary>
    /// Service discovery and communication
    /// </summary>
    public class ServiceDiscClient : IServiceDiscClient
    {
        private readonly List<ServiceInformation> _services = new List<ServiceInformation>();
        private readonly ServiceDiscNetworkResolver _networkResolver;

        private IServiceHostFactory _serviceHostFactory;

        internal IServiceDiscConnection ServiceDiscConnection { get; }

        public IServiceHostFactory ServiceHostFactory
        {
            get => _serviceHostFactory;
            set => _serviceHostFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Create client using <see cref="InMemoryServiceDiscConnection"/>.
        /// </summary>
        public ServiceDiscClient() : this(new InMemoryServiceDiscConnection())
        {
        }

        /// <summary>
        /// Constructor using a connection string. It should contain "ProviderName=" with the name of a class
        /// implementing <see cref="IServiceDiscConnection"/>, delimited by semicolon.
        /// </summary>
        /// <example>
        /// var serviceDisc = new ServiceDiscClient("ProviderName=AzureStorageServiceDiscConnection;DefaultEndpointsProtocol=https;AccountName=MyAccount;AccountKey=MyAccountKey;EndpointSuffix=core.windows.net");
        /// </example>
        /// <example>
        /// var serviceDisc = new ServiceDiscClient("ConnectionStringSettingName");
        /// </example>
        /// <param name="connectionString">Connection string to create <see cref="IServiceDiscConnection"/> from.</param>
        public ServiceDiscClient(string connectionString)
        {
            _networkResolver = ServiceDiscNetworkResolver.GetDefaultNetworkResolver();
            ServiceDiscConnection = ConnectionStringParser.Create(connectionString);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connection">Connection to store service information.</param>
        public ServiceDiscClient(IServiceDiscConnection connection)
        {
            _networkResolver = ServiceDiscNetworkResolver.GetDefaultNetworkResolver();
            ServiceDiscConnection = connection;

            ServiceHostFactory = new QueueServiceHostFactory();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connection">Connection to store service information.</param>
        /// <param name="networkResolver">Used to resolve the IP of hosted services.</param>
        public ServiceDiscClient(IServiceDiscConnection connection, ServiceDiscNetworkResolver networkResolver)
        {
            _networkResolver = networkResolver;
            ServiceDiscConnection = connection;

            ServiceHostFactory = new WebApiServiceHostFactory();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connection">Connection to store service information.</param>
        /// <param name="serviceHostFactory">Factory for hosting the services.</param>
        public ServiceDiscClient(IServiceDiscConnection connection, IServiceHostFactory serviceHostFactory)
        {
            _networkResolver = ServiceDiscNetworkResolver.GetDefaultNetworkResolver();
            ServiceDiscConnection = connection;

            ServiceHostFactory = serviceHostFactory;
        }

        /// <summary>
        /// Start a host for <paramref name="service"/> and register the service in the service list.
        /// </summary>
        /// <typeparam name="T">Type of service to host. This type will be used to resolve the service.</typeparam>
        /// <param name="service">An object to host as a service.</param>
        /// <param name="name">Name of service, can be used to resolve specific instances of a service.</param>
        /// <returns>Document which describes the hosted service.</returns>
        public async Task<ServiceInformation> HostAsync<T>(T service, string name = null)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            var host = ServiceHostFactory.CreateServiceHost(service, ServiceDiscConnection, _networkResolver);

            var serviceInformation = new ServiceInformation(typeof(T), host);
            serviceInformation.Id = Guid.NewGuid();
            serviceInformation.Name = name == "" ? null : name;

            _services.Add(serviceInformation);

            await ServiceDiscConnection.RegisterAsync(serviceInformation).ConfigureAwait(false);

            Trace.WriteLine($"{serviceInformation.Type} hosted on {host.Address}");

            return serviceInformation;
        }

        /// <summary>
        /// Remove the service from service list so that it won't be possible to resolve it any longer.
        /// </summary>
        /// <param name="serviceInformation">Document describing the service to unregister.</param>
        public async Task UnregisterAsync(ServiceInformation serviceInformation)
        {
            await ServiceDiscConnection.UnregisterAsync(serviceInformation.Id).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve a service of type <typeparamref name="T"/> and create a proxy for communicating with it.
        /// </summary>
        /// <typeparam name="T">Type of service to resolve.</typeparam>
        /// <returns>Proxy for communicating with the service.</returns>
        public async Task<T> GetAsync<T>() where T : class
        {
            var document = await ServiceDiscConnection.GetServiceListAsync().ConfigureAwait(false);

            var serviceName = typeof(T).FullName;
            var services = document.Services.Where(s => s.Type == serviceName).ToArray();
            if (!services.Any())
            {
                return null;
            }

            var proxyType = CreateServiceProxy<T>(services);

            return proxyType;
        }

        /// <summary>
        /// Resolve the service with <paramref name="id"/> of type <typeparamref name="T"/> and create a proxy for communicating with it.
        /// </summary>
        /// <typeparam name="T">Type of service to resolve.</typeparam>
        /// <paramref name="id">Id of service to resolve.</paramref>
        /// <returns>Proxy for communicating with the service.</returns>
        public async Task<T> GetAsync<T>(Guid id) where T : class
        {
            var document = await ServiceDiscConnection.GetServiceListAsync().ConfigureAwait(false);

            var serviceName = typeof(T).FullName;
            var services = document.Services.Where(s => s.Type == serviceName && s.Id == id).ToArray();
            if (!services.Any())
            {
                return null;
            }

            var proxyType = CreateServiceProxy<T>(services);

            return proxyType;
        }

        /// <summary>
        /// Resolve the service with <paramref name="name"/> of type <typeparamref name="T"/> and create a proxy for communicating with it.
        /// </summary>
        /// <typeparam name="T">Type of service to resolve.</typeparam>
        /// <paramref name="name">Name of service to resolve.</paramref>
        /// <returns>Proxy for communicating with the service.</returns>
        public async Task<T> GetAsync<T>(string name) where T : class
        {
            var document = await ServiceDiscConnection.GetServiceListAsync().ConfigureAwait(false);

            var serviceName = typeof(T).FullName;
            var services = document.Services.Where(s => s.Type == serviceName && s.Name == name).ToArray();
            if (!services.Any())
            {
                return null;
            }

            var proxyType = CreateServiceProxy<T>(services);

            return proxyType;
        }

        /// <summary>
        /// Send message <typeparamref name="T"/> to listeners.
        /// </summary>
        /// <typeparam name="T">Type of message to send.</typeparam>
        public async Task SendAsync<T>(T message) where T : class
        {
            await ServiceDiscConnection.SendMessageAsync(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Subscribe to messages of type <typeparamref name="T"/>
        /// </summary>
        /// <typeparam name="T">Type of message to listen to.</typeparam>
        /// <param name="callback">Callback for when message arrives.</param>
        public Task SubscribeAsync<T>(Action<T> callback) where T : class
        {
            return ServiceDiscConnection.SubscribeAsync(callback);
        }

        private T CreateServiceProxy<T>(ServiceInformation[] services) where T : class
        {
            var serviceDiscCollection = new ServiceDiscCollection(services);
            serviceDiscCollection.FailedCall += ServiceDiscCollectionOnFailedCall;

            var proxyGenerator = new ProxyGenerator();
            IInterceptor serviceProxy = new ServiceProxy(serviceDiscCollection, ServiceDiscConnection, TimeSpan.FromMinutes(5));

            var proxyType = proxyGenerator.CreateInterfaceProxyWithoutTarget<T>(serviceProxy);
            return proxyType;
        }

        private void ServiceDiscCollectionOnFailedCall(object sender, ServiceDiscCollection.FailedCallEventArgs failedCallEventArgs)
        {
            if (failedCallEventArgs.ServiceInformation.ExpireTime < DateTime.UtcNow)
            {
                ServiceDiscConnection.UnregisterAsync(failedCallEventArgs.ServiceInformation.Id).Wait();
            }
        }

        public void Dispose()
        {
            ServiceDiscConnection?.Dispose();
        }
    }
}
