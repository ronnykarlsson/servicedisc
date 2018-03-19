﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using ServiceDisc.Models;
using ServiceDisc.Networking.ServiceDiscConnection;
using ServiceDisc.Serialization;

namespace ServiceDisc.Networking.QueueService
{
    internal class QueueServiceClient : IServiceClient
    {
        private static readonly TypeSerializer _typeSerializer = new TypeSerializer();
        private static ConcurrentDictionary<IServiceDiscConnection, QueueServiceClientResponseQueue> _responseQueueDictionary = new ConcurrentDictionary<IServiceDiscConnection, QueueServiceClientResponseQueue>();

        public async Task CallServiceAsync(IServiceDiscConnection connection, ServiceInformation service, IInvocation invocation, CancellationToken cancellationToken)
        {
            var queueName = GetQueueName(service.Type);
            var parameters = BuildParameterDictionary(invocation);

            var responseQueue = _responseQueueDictionary.GetOrAdd(connection, c => new QueueServiceClientResponseQueue(c));

            var messageId = Guid.NewGuid().ToString();
            var message = new QueueServiceRequestMessage(invocation.Method.Name, parameters, responseQueue.ClientId, messageId);

            string responseString = null;
            responseQueue.Subscribe(messageId, response =>
            {
                responseString = response;
            });

            await connection.SendMessageAsync(message, queueName).ConfigureAwait(false);

            var timeout = DateTime.UtcNow.Add(TimeSpan.FromSeconds(60));
            while (responseString == null)
            {
                if (DateTime.UtcNow > timeout)
                {
                    throw new TimeoutException("No response from QueueServiceHost.");
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (invocation.Method.ReturnType != null)
            {
                var deserializedResult = _typeSerializer.Deserialize(responseString, invocation.Method.ReturnType);
                invocation.ReturnValue = deserializedResult;
            }
        }

        private string GetQueueName(string serviceType)
        {
            var queueName = serviceType.Replace(".", "-").ToLowerInvariant();

            queueName += "-qsh";

            if (queueName.Length > 63)
            {
                queueName = queueName.Substring(queueName.Length - 63, 63);
                queueName.Trim('-');
            }

            return queueName;
        }

        private Dictionary<string, string> BuildParameterDictionary(IInvocation invocation)
        {
            var dictionary = new Dictionary<string, string>();

            var parameters = invocation.Method.GetParameters();

            for (var i = 0; i < parameters.Length; i++)
            {
                dictionary.Add(parameters[i].Name, _typeSerializer.Serialize(invocation.Arguments[i]));
            }
            return dictionary;
        }
    }
}