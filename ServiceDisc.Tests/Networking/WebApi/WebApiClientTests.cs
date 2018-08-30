﻿using ServiceDisc.Networking.ServiceDiscConnection;
using ServiceDisc.Networking.WebApi;
using Xunit;

namespace ServiceDisc.Tests.Networking.WebApi
{
    public class WebApiClientTests
    {
        [Theory]
        [InlineData("123abc\nA\0BC\tok\r\na\n\rb")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData(@"test#¤€""'\TEST10´!a{0}/")]
        public void EchoStrings(string input)
        {
            var echoService = CreateEchoService();

            var result = echoService.Send(input);
            Assert.Equal(input, result);
        }

        private static IEchoService CreateEchoService()
        {
            var serviceDisc = new ServiceDiscClient(new InMemoryServiceDiscConnection())
                {ServiceHostFactory = new WebApiServiceHostFactory()};
            serviceDisc.HostAsync<IEchoService>(new EchoService()).GetAwaiter().GetResult();
            var echoService = serviceDisc.GetAsync<IEchoService>().GetAwaiter().GetResult();
            return echoService;
        }

        public interface IEchoService
        {
            string Send(string input);
        }

        public class EchoService : IEchoService
        {
            public string Send(string input)
            {
                return input;
            }
        }
    }
}
