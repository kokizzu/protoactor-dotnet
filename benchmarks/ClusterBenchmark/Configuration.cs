// -----------------------------------------------------------------------
// <copyright file="Configuration.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Threading.Tasks;
using ClusterExperiment1.Messages;
using k8s;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Proto;
using Proto.Cluster;
using Proto.Cluster.Consul;
using Proto.Cluster.Identity;
using Proto.Cluster.Identity.MongoDb;
using Proto.Cluster.Identity.Redis;
using Proto.Cluster.Kubernetes;
using Proto.Remote;
using Proto.Remote.GrpcCore;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using Log = Serilog.Log;

namespace ClusterExperiment1
{
    public static class Configuration
    {
        private static ClusterConfig GetClusterConfig(
            IClusterProvider clusterProvider,
            IIdentityLookup identityLookup
        )
        {
            var helloProps = Props.FromProducer(() => new WorkerActor());
            return ClusterConfig
                .Setup("mycluster", clusterProvider, identityLookup)
                .WithClusterKind("hello", helloProps);
        }

        private static GrpcCoreRemoteConfig GetRemoteConfig()
        {
            var portStr = Environment.GetEnvironmentVariable("PROTOPORT") ?? $"{RemoteConfigBase.AnyFreePort}";
            var port = int.Parse(portStr);
            var host = Environment.GetEnvironmentVariable("PROTOHOST") ?? RemoteConfigBase.Localhost;
            var advertisedHost = Environment.GetEnvironmentVariable("PROTOHOSTPUBLIC");

            var remoteConfig = GrpcCoreRemoteConfig
                .BindTo(host, port)
                .WithAdvertisedHost(advertisedHost)
                .WithProtoMessages(MessagesReflection.Descriptor)
                .WithEndpointWriterMaxRetries(2);
            
            return remoteConfig;
        }

        private static IClusterProvider ClusterProvider()
        {
            try
            {
                var kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
                Console.WriteLine("Running with Kubernetes Provider");
                return new KubernetesProvider(kubernetes);
            }
            catch
            {
                Console.WriteLine("Running with Consul Provider");
                return new ConsulProvider(new ConsulProviderConfig());
            }
        }

        public static IIdentityLookup GetIdentityLookup() => GetMongoIdentityLookup();//  GetRedisIdentityLookup();// new PartitionIdentityLookup(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));

        private static IIdentityLookup GetRedisIdentityLookup()
        {
            var multiplexer = ConnectionMultiplexer.Connect("localhost:6379");
            var redisIdentityStorage = new RedisIdentityStorage("mycluster", multiplexer,maxConcurrency:50);

            return new IdentityStorageLookup(redisIdentityStorage);
        }

        private static IIdentityLookup GetMongoIdentityLookup()
        {
            var db = GetMongo();
            var identity = new IdentityStorageLookup(
                new MongoIdentityStorage("mycluster", db.GetCollection<PidLookupEntity>("pids"), 200)
            );
            return identity;
        }

        private static IMongoDatabase GetMongo()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MONGO") ?? "mongodb://127.0.0.1:27017/ProtoMongo";
            var url = MongoUrl.Create(connectionString);
            var settings = MongoClientSettings.FromUrl(url);
            // settings.WaitQueueSize = 10000;
            // settings.WaitQueueTimeout = TimeSpan.FromSeconds(10);
            //
            // settings.WriteConcern = WriteConcern.WMajority;
            // settings.ReadConcern = ReadConcern.Majority;
            var client = new MongoClient(settings);
            var database = client.GetDatabase("ProtoMongo");
            return database;
        }

        public static async Task<Cluster> SpawnMember()
        {
            var system = new ActorSystem(new ActorSystemConfig().WithDeadLetterThrottleCount(3)
                .WithDeadLetterThrottleInterval(TimeSpan.FromSeconds(1))
                .WithDeadLetterRequestLogging(false)
            );
            system.EventStream.Subscribe<ClusterTopology>(e => {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"{system.Id}-ClusterTopology:{e.GetMembershipHashCode()}");
                Console.ResetColor();
            });
            system.EventStream.Subscribe<LeaderElected>(e => {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"{system.Id}-Leader:{e.Leader.Id}");
                Console.ResetColor();
            });
            var clusterProvider = ClusterProvider();
            var identity = GetIdentityLookup();
            
            system.WithRemote(GetRemoteConfig()).WithCluster(GetClusterConfig(clusterProvider,identity));
            await system.Cluster().StartMemberAsync();
            return system.Cluster();
        }

        public static async Task<Cluster> SpawnClient()
        {
            var system = new ActorSystem(new ActorSystemConfig().WithDeadLetterThrottleCount(3)
                .WithDeadLetterThrottleInterval(TimeSpan.FromSeconds(1))
                .WithDeadLetterRequestLogging(false)
            );
            system.EventStream.Subscribe<ClusterTopology>(e => {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"{system.Id}-ClusterTopology:{e.GetMembershipHashCode()}");
                Console.ResetColor();
            });
            system.EventStream.Subscribe<LeaderElected>(e => {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"{system.Id}-Leader:{e.Leader.Id}");
                Console.ResetColor();
            });
            var clusterProvider = ClusterProvider();
            var identity = GetIdentityLookup();
            system.WithRemote(GetRemoteConfig()).WithCluster(GetClusterConfig(clusterProvider,identity));

            await system.Cluster().StartClientAsync();
            return system.Cluster();
        }

        public static void SetupLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(LogEventLevel.Error)
                .CreateLogger();
            
            Proto.Log.SetLoggerFactory(LoggerFactory.Create(l =>
                    l.AddSerilog().SetMinimumLevel(LogLevel.Error)
                )
            );
        }
    }
}