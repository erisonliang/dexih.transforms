﻿using dexih.connections.test;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace dexih.connections.azure
{
    public class ConnectionAzureTests
    {
        public ConnectionAzureTable GetConnection()
        {
            return new ConnectionAzureTable()
            {
                //Name = "Test Connection",
                //ServerName = Convert.ToString(Helpers.AppSettings["Azure:ServerName"]),
                //UserName = Convert.ToString(Helpers.AppSettings["Azure:UserName"]),
                //Password = Convert.ToString(Helpers.AppSettings["Azure:Password"]),
                UseConnectionString = true,
                ConnectionString = "UseDevelopmentStorage=true"
            };


        }

        [Fact]
        public async Task Azure_Basic()
        {
            string database = "Test-" + Guid.NewGuid().ToString();

            await new UnitTests().Unit(GetConnection(), database);
        }

        [Fact]
        public async Task Azure_Transform()
        {
            string database = "Test-" + Guid.NewGuid().ToString();

            await new TransformTests().Transform(GetConnection(), database);
        }

        [Fact]
        public async Task Azure_Performance()
        {
            await new PerformanceTests().Performance(GetConnection(), "Test-" + Guid.NewGuid().ToString(), 100);
        }



    }
}
