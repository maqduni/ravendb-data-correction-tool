﻿using System;
using System.Threading.Tasks;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Server.Rachis
{
    public class BasicTests : RachisConsensusTestBase
    {
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)] 
        [InlineData(7)] 
        public async Task CanApplyCommitAcrossAllCluster(int amount)
        {
            var leader = await CreateNetworkAndGetLeader(amount);
            SetupPredicateForCluster((machine, context) => machine.Read(context, "test") == 10);
            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (var i = 0; i < 5; i++)
                {
                    await leader.PutAsync(ctx.ReadObject(new DynamicJsonValue
                    {
                        ["Name"] = "test",
                        ["Value"] = i 
                    }, "test"));
                }
            }
            await WaitOnPredicateForCluster(TimeSpan.FromSeconds(150));
        }
    }
}