using System;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Ondewo.Vtsi;
using Xunit;

namespace Ondewo.Vtsi.Client.Tests
{
    /// <summary>
    /// The product-specific half of the suite: concrete assertions against the ONDEWO VTSI API,
    /// spelled out with real message, field, enum and RPC names.
    /// <para>
    /// This is the only test file that has to be rewritten when the setup is replicated to another
    /// ONDEWO product - <see cref="GeneratedStubsTests"/> carries over unchanged.
    /// </para>
    /// </summary>
    public class VtsiStubsTests
    {
        private const string DummyTarget = "http://localhost:50051";

        /// <summary>Every RPC <c>ondewo.vtsi.Projects</c> declares, in declaration order.</summary>
        private static readonly string[] ProjectsRpcs =
        {
            "CreateVtsiProject", "GetVtsiProject", "UpdateVtsiProject", "DeleteVtsiProject",
            "DeployVtsiProject", "UndeployVtsiProject", "ListVtsiProjects",
        };

        /// <summary>Every RPC <c>ondewo.vtsi.Logs</c> declares, in declaration order.</summary>
        private static readonly string[] LogsRpcs =
        {
            "StreamCallLogs", "ListCallLogs", "GetCallLogStream", "ListCallLogStreams",
            "DeleteCallLogs",
        };

        /// <summary>The one server-streaming RPC in the whole VTSI API.</summary>
        private const string StreamingRpc = "StreamCallLogs";

        [Fact]
        public void CallerRoundTripsItsScalarsAndItsNestedSipConfig()
        {
            var caller = new Caller
            {
                Name = "projects/p1/callers/c1",
                CallName = "outbound-42",
                SipCallerConfig = new SipCallerConfig
                {
                    CalleeId = "sip:+4312345678@ondewo.com",
                    SipBaseConfig = new SipBaseConfig { SipSimVersion = "1.2.3" },
                },
            };
            caller.SipCallerConfig.SipHeaders.Add("X-Ondewo-Campaign", "spring-2026");

            byte[] bytes = caller.ToByteArray();
            Caller parsed = Caller.Parser.ParseFrom(bytes);

            Assert.NotEmpty(bytes);
            Assert.Equal(caller, parsed);
            Assert.Equal("projects/p1/callers/c1", parsed.Name);
            Assert.Equal("outbound-42", parsed.CallName);
            Assert.Equal("sip:+4312345678@ondewo.com", parsed.SipCallerConfig.CalleeId);
            Assert.Equal("1.2.3", parsed.SipCallerConfig.SipBaseConfig.SipSimVersion);
            Assert.Equal("spring-2026", Assert.Single(parsed.SipCallerConfig.SipHeaders).Value);
        }

        [Fact]
        public void VtsiProjectRoundTripsEveryScalarFieldKind()
        {
            var project = new VtsiProject
            {
                Name = "projects/p1",
                DisplayName = "Spring campaign",
                MaxCallers = 12,
                MaxListeners = 4,
                VtsiProjectStatus = VtsiProjectStatus.Deployed,
                CreatedBy = "user-1",
                AsteriskPort = 5060,
            };
            project.NluAgentNames.Add("projects/nlu-1/agent");

            VtsiProject parsed = VtsiProject.Parser.ParseFrom(project.ToByteArray());

            Assert.Equal(project, parsed);
            Assert.Equal("projects/p1", parsed.Name);
            Assert.Equal("Spring campaign", parsed.DisplayName);
            Assert.Equal(12, parsed.MaxCallers);
            Assert.Equal(4, parsed.MaxListeners);
            Assert.Equal(VtsiProjectStatus.Deployed, parsed.VtsiProjectStatus);
            Assert.Equal(5060, parsed.AsteriskPort);
            Assert.Equal(new[] { "projects/nlu-1/agent" }, parsed.NluAgentNames);
        }

        [Fact]
        public void RepeatedFieldRoundTripsThroughAListResponse()
        {
            var response = new ListCallersResponse { NextPageToken = "page-2" };
            response.Callers.Add(new Caller { Name = "projects/p1/callers/c1" });
            response.Callers.Add(new Caller { Name = "projects/p1/callers/c2" });

            ListCallersResponse parsed = ListCallersResponse.Parser.ParseFrom(response.ToByteArray());

            Assert.Equal(response, parsed);
            Assert.Equal("page-2", parsed.NextPageToken);
            Assert.Equal(
                new[] { "projects/p1/callers/c1", "projects/p1/callers/c2" },
                parsed.Callers.Select(caller => caller.Name));
        }

        [Fact]
        public void UnsetScalarFieldsCarryTheProto3DefaultsAndStayOffTheWire()
        {
            var project = new VtsiProject();

            Assert.Equal(string.Empty, project.Name);
            Assert.Equal(0, project.MaxCallers);
            Assert.Equal(VtsiProjectStatus.Unspecified, project.VtsiProjectStatus);
            Assert.Null(project.CreatedAt);
            Assert.Empty(project.NluAgentNames);
            Assert.Empty(project.ToByteArray());
        }

        /// <summary>
        /// The VTSI API leans hard on proto3 <c>optional</c>: a config field that was never set
        /// has to stay distinguishable from one explicitly set to <c>false</c> / <c>0</c>, or a
        /// partial config update would silently reset the server's defaults. This is exactly the
        /// property the angular target needed a codemod for; the csharp generator emits hasBits
        /// for it, and this case is what keeps that true.
        /// </summary>
        [Fact]
        public void ExplicitPresenceSurvivesAFalseAndAZero()
        {
            var unset = new InterruptionHandlingConfig();
            var explicitlyOff = new InterruptionHandlingConfig
            {
                Enabled = false,
                BackoffSeconds = 0f,
            };

            Assert.False(unset.HasEnabled);
            Assert.False(unset.HasBackoffSeconds);
            Assert.True(explicitlyOff.HasEnabled);
            Assert.True(explicitlyOff.HasBackoffSeconds);
            Assert.False(explicitlyOff.Enabled);
            Assert.Equal(0f, explicitlyOff.BackoffSeconds);

            // The distinction has to survive the wire, not just the in-memory object: an unset
            // optional field writes nothing, an explicit default writes its tag and value.
            Assert.Empty(unset.ToByteArray());
            Assert.NotEmpty(explicitlyOff.ToByteArray());
            Assert.NotEqual(unset.ToByteArray(), explicitlyOff.ToByteArray());

            InterruptionHandlingConfig parsed =
                InterruptionHandlingConfig.Parser.ParseFrom(explicitlyOff.ToByteArray());

            Assert.True(parsed.HasEnabled);
            Assert.True(parsed.HasBackoffSeconds);
            Assert.False(parsed.Enabled);

            // Generated Equals compares field VALUES, not presence, so these two DO compare equal
            // even though they are different on the wire. Asserting the bytes - not Equals - is
            // therefore the only way to pin explicit presence down.
            Assert.Equal(unset, parsed);

            parsed.ClearEnabled();
            parsed.ClearBackoffSeconds();
            Assert.False(parsed.HasEnabled);
            Assert.Equal(unset.ToByteArray(), parsed.ToByteArray());
        }

        /// <summary>
        /// <c>CallView</c> is the counter-example to the usual ONDEWO convention: its zero value is
        /// <c>MINIMUM</c>, a real view, not an "unspecified" placeholder - which is precisely why a
        /// client that cannot send a zero-valued enum is broken. It can be sent here.
        /// </summary>
        [Fact]
        public void EnumsStartAtTheirZeroValue()
        {
            Assert.Equal(0, (int)CallView.Minimum);
            Assert.Equal(CallView.Minimum, default(CallView));
            Assert.Equal(0, (int)CallStatus.Unspecified);
            Assert.Equal(0, (int)VtsiProjectStatus.Unspecified);
            Assert.Equal(0, (int)VtsiProjectView.Unspecified);
            Assert.Equal(0, (int)VtsiProjectSortingMode.Ascending);

            // The C# name is PascalCased; the wire/JSON name is the one the server speaks.
            Assert.Equal("MINIMUM", OriginalNameOf(CallView.Minimum));
            Assert.Equal("CALL_STATUS_UNSPECIFIED", OriginalNameOf(CallStatus.Unspecified));
            Assert.Equal("VTSI_PROJECT_VIEW_MINIMUM", OriginalNameOf(VtsiProjectView.Minimum));
        }

        [Fact]
        public void EnumFieldRoundTripsANonDefaultValue()
        {
            var project = new VtsiProject { VtsiProjectStatus = VtsiProjectStatus.Undeploying };

            VtsiProject parsed = VtsiProject.Parser.ParseFrom(project.ToByteArray());

            Assert.Equal(VtsiProjectStatus.Undeploying, parsed.VtsiProjectStatus);
            Assert.NotEmpty(project.ToByteArray());
        }

        /// <summary>
        /// <c>ondewo.vtsi.Calls</c> is the product's main service and by far the largest: every one
        /// of its 28 RPCs is unary, so each gets a blocking and an <c>...Async</c> client method.
        /// </summary>
        [Fact]
        public void CallsClientBindsToAChannelAndExposesEveryDeclaredRpcTwice()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            var client = new Calls.CallsClient(channel);

            Assert.NotNull(client);
            Assert.Equal("ondewo.vtsi.Calls", Calls.Descriptor.FullName);
            Assert.Equal(28, Calls.Descriptor.Methods.Count);
            Assert.Contains(Calls.Descriptor.Methods, method => method.Name == "StartCaller");

            string[] clientMethods = typeof(Calls.CallsClient)
                .GetMethods()
                .Select(method => method.Name)
                .Distinct()
                .ToArray();

            foreach (MethodDescriptor rpc in Calls.Descriptor.Methods)
            {
                Assert.Equal(MethodType.Unary, MethodTypeOf(typeof(Calls), rpc.Name));
                Assert.Contains(rpc.Name, clientMethods);
                Assert.Contains(rpc.Name + "Async", clientMethods);
            }
        }

        [Fact]
        public void ProjectsAndLogsClientsAreGeneratedForTheOtherTwoServicesToo()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            var projects = new Projects.ProjectsClient(channel);
            var logs = new Logs.LogsClient(channel);

            Assert.NotNull(projects);
            Assert.NotNull(logs);
            Assert.Equal("ondewo.vtsi.Projects", Projects.Descriptor.FullName);
            Assert.Equal("ondewo.vtsi.Logs", Logs.Descriptor.FullName);
            Assert.Equal(ProjectsRpcs, Projects.Descriptor.Methods.Select(method => method.Name));
            Assert.Equal(LogsRpcs, Logs.Descriptor.Methods.Select(method => method.Name));
        }

        /// <summary>
        /// <c>StreamCallLogs</c> is the one streaming RPC in the VTSI API. grpc_csharp_plugin gives
        /// a streaming RPC exactly one client method - the async streaming call - and deliberately
        /// no <c>...Async</c> twin, so asserting one here would be asserting a bug.
        /// </summary>
        [Fact]
        public void TheOneStreamingRpcIsGeneratedAsAServerStreamingCall()
        {
            Assert.Equal(MethodType.ServerStreaming, MethodTypeOf(typeof(Logs), StreamingRpc));

            string[] clientMethods = typeof(Logs.LogsClient)
                .GetMethods()
                .Select(method => method.Name)
                .Distinct()
                .ToArray();

            Assert.Contains(StreamingRpc, clientMethods);
            Assert.DoesNotContain(StreamingRpc + "Async", clientMethods);

            Type[] returnTypes = typeof(Logs.LogsClient)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == StreamingRpc)
                .Select(method => method.ReturnType)
                .Distinct()
                .ToArray();

            Assert.Equal(
                new[] { typeof(AsyncServerStreamingCall<StreamCallLogsResponse>) },
                returnTypes);

            foreach (string rpc in LogsRpcs.Where(name => name != StreamingRpc))
            {
                Assert.Equal(MethodType.Unary, MethodTypeOf(typeof(Logs), rpc));
                Assert.Contains(rpc + "Async", clientMethods);
            }
        }

        /// <summary>
        /// VTSI drives a whole phone call, so its API imports NLU, QA, S2T, SIP and T2S and the
        /// compiler emits all of them into THIS assembly. That is correct, not bloat - a VTSI
        /// consumer reaches those stubs without a second package reference.
        /// </summary>
        [Fact]
        public void TheAssemblyAlsoShipsTheVendoredNluQaS2tSipAndT2sStubs()
        {
            string[] services = ProductStubs.Assembly.GetTypes()
                .Select(type => type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
                .Where(property => property != null && property.PropertyType == typeof(ServiceDescriptor))
                .Select(property => ((ServiceDescriptor)property.GetValue(null)).FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            foreach (string service in new[]
                     {
                         "ondewo.vtsi.Calls", "ondewo.vtsi.Projects", "ondewo.vtsi.Logs",
                         "ondewo.nlu.Sessions", "ondewo.qa.QA", "ondewo.s2t.Speech2Text",
                         "ondewo.sip.Sip", "ondewo.t2s.Text2Speech",
                     })
            {
                Assert.Contains(service, services);
            }

            // The vendored messages are the very types the VTSI configs refer to.
            Assert.Same(ProductStubs.Assembly, typeof(global::Ondewo.S2T.TranscribeRequestConfig).Assembly);
            Assert.Same(ProductStubs.Assembly, typeof(global::Ondewo.T2S.RequestConfig).Assembly);
        }

        private static MethodType MethodTypeOf(Type serviceClass, string rpc)
        {
            FieldInfo field = serviceClass.GetField(
                "__Method_" + rpc, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);

            return Assert.IsAssignableFrom<IMethod>(field.GetValue(null)).Type;
        }

        private static string OriginalNameOf<TEnum>(TEnum value)
            where TEnum : struct, Enum
        {
            return typeof(TEnum)
                .GetField(value.ToString())
                .GetCustomAttributes(typeof(OriginalNameAttribute), false)
                .Cast<OriginalNameAttribute>()
                .Single()
                .Name;
        }
    }
}
