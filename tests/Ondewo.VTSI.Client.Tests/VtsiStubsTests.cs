using System;
using System.Collections.Generic;
using System.IO;
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

        // The RPC lists below are MUST-CONTAIN pins - RPCs consumers rely on, in declaration order - and
        // never the whole surface: the complete set of every service is DERIVED from the protos (or the
        // committed stubs) by VtsiApiSurface, so the next API release adds RPCs without editing them.

        /// <summary>RPCs <c>ondewo.vtsi.Projects</c> must declare.</summary>
        private static readonly string[] ProjectsRpcs =
        {
            "CreateVtsiProject", "GetVtsiProject", "UpdateVtsiProject", "DeleteVtsiProject",
            "DeployVtsiProject", "UndeployVtsiProject", "ListVtsiProjects",
        };

        /// <summary>RPCs <c>ondewo.vtsi.Logs</c> must declare.</summary>
        private static readonly string[] LogsRpcs =
        {
            "StreamCallLogs", "ListCallLogs", "GetCallLogStream", "ListCallLogStreams",
            "DeleteCallLogs", "ExportCallLogs",
        };

        /// <summary>The live server-streaming RPC of <c>ondewo.vtsi.Logs</c>.</summary>
        private const string StreamingRpc = "StreamCallLogs";

        /// <summary>Server-streaming RPCs <c>ondewo.vtsi.Logs</c> must declare (9.1.0 adds <c>ExportCallLogs</c>).</summary>
        private static readonly string[] LogsStreamingRpcs = { StreamingRpc, "ExportCallLogs" };

        /// <summary>RPCs <c>ondewo.vtsi.Softphones</c> must declare (as of 9.1.0).</summary>
        private static readonly string[] SoftphonesRpcs =
        {
            "CreateSoftphoneAccount", "GetSoftphoneAccount", "UpdateSoftphoneAccount", "DeleteSoftphoneAccount",
            "ListSoftphoneAccounts", "RotateSoftphoneCredentials", "ListSoftphoneCertificates",
            "GetSoftphoneCertificate", "RevokeSoftphoneCertificate", "GetSoftphoneProvisioning",
            "GetSoftphoneRegistration",
        };

        /// <summary>RPCs <c>ondewo.vtsi.Campaigns</c> must declare (as of 9.1.0).</summary>
        private static readonly string[] CampaignsRpcs =
        {
            "CreateCampaign", "GetCampaign", "UpdateCampaign", "DeleteCampaign", "ListCampaigns",
            "GetCampaignStatistics", "ListCampaignCalls", "StartCampaign", "StopCampaign", "HardStopCampaign",
            "ResumeCampaign", "StreamCampaignStatus", "GetCampaignCall", "CancelCampaignCalls", "RetryCampaignCalls",
            "CloneCampaign",
        };

        /// <summary>RPCs <c>ondewo.vtsi.Events</c> must declare (as of 9.1.0).</summary>
        private static readonly string[] EventsRpcs =
        {
            "CreateVtsiEventSubscription", "GetVtsiEventSubscription", "UpdateVtsiEventSubscription",
            "DeleteVtsiEventSubscription", "ListVtsiEventSubscriptions", "CreateWebhook", "GetWebhook",
            "UpdateWebhook", "DeleteWebhook", "ListWebhooks", "TestWebhook", "RedeliverWebhookEvents",
            "SubscribeVtsiEvents",
        };

        /// <summary>Streaming RPCs <c>ondewo.vtsi.Calls</c> must declare (as of 9.1.0), with their method types.</summary>
        private static readonly (string Rpc, MethodType Type)[] CallsStreamingRpcs =
        {
            ("StreamCallerStatus", MethodType.ServerStreaming),
            ("StreamListenerStatus", MethodType.ServerStreaming),
            ("StreamScheduledCallerStatus", MethodType.ServerStreaming),
            ("StreamCallAudio", MethodType.DuplexStreaming),
            ("ListenCallAudio", MethodType.ServerStreaming),
        };

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
        /// <c>ondewo.vtsi.Calls</c> is the product's main service and by far the largest. Each unary RPC
        /// gets a blocking and an <c>...Async</c> client method; each streaming one (status streams and
        /// live call audio) gets exactly one. The full RPC set is derived (<see cref="VtsiApiSurface"/>).
        /// </summary>
        [Fact]
        public void CallsClientBindsToAChannelAndExposesEveryDeclaredRpcTwice()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            var client = new Calls.CallsClient(channel);

            Assert.NotNull(client);
            Assert.Equal("ondewo.vtsi.Calls", Calls.Descriptor.FullName);
            Assert.Equal(VtsiApiSurface.RpcNamesOf("ondewo.vtsi.Calls"), Calls.Descriptor.Methods.Select(method => method.Name));
            Assert.Contains(Calls.Descriptor.Methods, method => method.Name == "StartCaller");
            foreach ((string rpc, MethodType type) in CallsStreamingRpcs)
            {
                Assert.Equal(type, MethodTypeOf(typeof(Calls), rpc));
            }

            string[] clientMethods = typeof(Calls.CallsClient)
                .GetMethods()
                .Select(method => method.Name)
                .Distinct()
                .ToArray();

            foreach (MethodDescriptor rpc in Calls.Descriptor.Methods)
            {
                Assert.Contains(rpc.Name, clientMethods);
                if (rpc.IsClientStreaming || rpc.IsServerStreaming)
                {
                    Assert.NotEqual(MethodType.Unary, MethodTypeOf(typeof(Calls), rpc.Name));
                    Assert.DoesNotContain(rpc.Name + "Async", clientMethods);
                }
                else
                {
                    Assert.Equal(MethodType.Unary, MethodTypeOf(typeof(Calls), rpc.Name));
                    Assert.Contains(rpc.Name + "Async", clientMethods);
                }
            }

            foreach (string rpc in new[]
                     {
                         "AddCallersToCampaign", "AddScheduledCallersToCampaign", "InviteToCall",
                         "RemoveCallParticipant", "SetCallMediaControl", "UpdateCaller", "UpdateCallers",
                         "UpdateListener", "UpdateListeners", "UpdateScheduledCaller", "CancelScheduledCallers",
                         "RetryScheduledCaller", "GetCallStatistics", "UpdateCall", "UpdateCalls",
                     })
            {
                Assert.Equal(MethodType.Unary, MethodTypeOf(typeof(Calls), rpc));
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
            Assert.Equal(VtsiApiSurface.RpcNamesOf("ondewo.vtsi.Projects"), Projects.Descriptor.Methods.Select(method => method.Name));
            Assert.Equal(VtsiApiSurface.RpcNamesOf("ondewo.vtsi.Logs"), Logs.Descriptor.Methods.Select(method => method.Name));
            AssertContainsInOrder(ProjectsRpcs, Projects.Descriptor.Methods.Select(method => method.Name));
            AssertContainsInOrder(LogsRpcs, Logs.Descriptor.Methods.Select(method => method.Name));
        }

        /// <summary>
        /// The three services VTSI API 9.0.0 adds - <c>Softphones</c>, <c>Campaigns</c> and
        /// <c>Events</c> - are generated, bind to a channel and expose every declared RPC: unary ones
        /// twice, the server streams <c>StreamCampaignStatus</c> and <c>SubscribeVtsiEvents</c> once.
        /// </summary>
        [Fact]
        public void SoftphonesCampaignsAndEventsClientsAreGenerated()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            Assert.NotNull(new Softphones.SoftphonesClient(channel));
            Assert.NotNull(new Campaigns.CampaignsClient(channel));
            Assert.NotNull(new Events.EventsClient(channel));
            Assert.Equal("ondewo.vtsi.Softphones", Softphones.Descriptor.FullName);
            Assert.Equal("ondewo.vtsi.Campaigns", Campaigns.Descriptor.FullName);
            Assert.Equal("ondewo.vtsi.Events", Events.Descriptor.FullName);
            AssertContainsInOrder(SoftphonesRpcs, Softphones.Descriptor.Methods.Select(method => method.Name));
            AssertContainsInOrder(CampaignsRpcs, Campaigns.Descriptor.Methods.Select(method => method.Name));
            AssertContainsInOrder(EventsRpcs, Events.Descriptor.Methods.Select(method => method.Name));

            foreach ((Type service, Type client, string[] rpcs, string streamingRpc) in new[]
                     {
                         (typeof(Softphones), typeof(Softphones.SoftphonesClient), SoftphonesRpcs, null),
                         (typeof(Campaigns), typeof(Campaigns.CampaignsClient), CampaignsRpcs, "StreamCampaignStatus"),
                         (typeof(Events), typeof(Events.EventsClient), EventsRpcs, "SubscribeVtsiEvents"),
                     })
            {
                string[] clientMethods = client.GetMethods().Select(method => method.Name).Distinct().ToArray();
                foreach (string rpc in rpcs)
                {
                    Assert.Contains(rpc, clientMethods);
                    if (rpc == streamingRpc)
                    {
                        Assert.Equal(MethodType.ServerStreaming, MethodTypeOf(service, rpc));
                        Assert.DoesNotContain(rpc + "Async", clientMethods);
                    }
                    else
                    {
                        Assert.Equal(MethodType.Unary, MethodTypeOf(service, rpc));
                        Assert.Contains(rpc + "Async", clientMethods);
                    }
                }
            }
        }

        /// <summary>
        /// Breaking in 9.0.0: <c>AsteriskConfigsFiles.sip_conf_file_string</c> is now
        /// <c>pjsip_conf_file_string</c> - same field number 1 and type, so the wire bytes do not
        /// change, but the C# property moved from <c>SipConfFileString</c> to
        /// <c>PjsipConfFileString</c>.
        /// </summary>
        [Fact]
        public void PjsipConfFileStringReplacesSipConfFileStringOnTheSameFieldNumber()
        {
            var files = new AsteriskConfigsFiles { PjsipConfFileString = "[transport-tls]" };

            AsteriskConfigsFiles parsed = AsteriskConfigsFiles.Parser.ParseFrom(files.ToByteArray());

            Assert.Equal("[transport-tls]", parsed.PjsipConfFileString);
            Assert.Equal(1, AsteriskConfigsFiles.PjsipConfFileStringFieldNumber);
            Assert.Null(typeof(AsteriskConfigsFiles).GetProperty("SipConfFileString"));
            Assert.Equal(
                "pjsip_conf_file_string",
                AsteriskConfigsFiles.Descriptor.FindFieldByNumber(1).Name);
        }

        /// <summary>
        /// Breaking in 9.0.0: eleven singular scalars of <c>calls.proto</c> gained explicit presence,
        /// so an explicit default is now distinguishable from unset (<c>Has*</c> / <c>Clear*</c>).
        /// </summary>
        [Fact]
        public void TheElevenCallsScalarsGainedExplicitPresence()
        {
            foreach ((MessageDescriptor message, string field) in new[]
                     {
                         (InterruptionHandlingConfig.Descriptor, "transcribe_on_disabled_interruptions"),
                         (TurnDetectionConfig.Descriptor, "turn_detection_system_prompt"),
                         (TurnDetectionConfig.Descriptor, "turn_detection_user_prompt"),
                         (AudioObjectStorageConfig.Descriptor, "activate_audio_object_storage"),
                         (AudioObjectStorageServicesActivationConfig.Descriptor, "activate_s2t"),
                         (AudioObjectStorageServicesActivationConfig.Descriptor, "activate_t2s"),
                         (MessageBrokerConfig.Descriptor, "activate_message_broker"),
                         (MessageBrokerServicesActivationConfig.Descriptor, "activate_s2t"),
                         (MessageBrokerServicesActivationConfig.Descriptor, "activate_nlu"),
                         (MessageBrokerServicesActivationConfig.Descriptor, "activate_t2s"),
                         (MessageBrokerServicesActivationConfig.Descriptor, "activate_sip"),
                     })
            {
                FieldDescriptor descriptor = message.FindFieldByName(field);
                Assert.NotNull(descriptor);
                Assert.True(descriptor.HasPresence, $"{message.Name}.{field} has no explicit presence");
            }

            var explicitlyOff = new MessageBrokerServicesActivationConfig { ActivateSip = false };
            Assert.True(explicitlyOff.HasActivateSip);
            Assert.NotEmpty(explicitlyOff.ToByteArray());
            Assert.True(MessageBrokerServicesActivationConfig.Parser.ParseFrom(explicitlyOff.ToByteArray()).HasActivateSip);
            Assert.False(new MessageBrokerServicesActivationConfig().HasActivateSip);
        }

        [Fact]
        public void SipTrunkTransportAndCertificateFieldsRoundTrip()
        {
            var variables = new AsteriskConfigsVariables
            {
                SipTrunkTransport = SipTrunkTransport.Udp,
                SipTrunkSourceCidr = "203.0.113.7/32",
                SipTrunkCaCertificatesPem = "-----BEGIN CERTIFICATE-----",
                SipTrunkVerifyServer = true,
            };
            variables.SoftphonePermitCidrs.Add("10.0.0.0/8");

            AsteriskConfigsVariables parsed = AsteriskConfigsVariables.Parser.ParseFrom(variables.ToByteArray());

            Assert.Equal(variables, parsed);
            Assert.Equal(0, (int)SipTrunkTransport.Unspecified);
            Assert.True(parsed.HasSipTrunkSourceCidr);
            Assert.False(new AsteriskConfigsVariables().HasSipTrunkSourceCidr);
            Assert.Equal("10.0.0.0/8", Assert.Single(parsed.SoftphonePermitCidrs));
        }

        /// <summary>
        /// <c>StreamCallLogs</c> and <c>ExportCallLogs</c> are the streaming RPCs of <c>ondewo.vtsi.Logs</c>.
        /// grpc_csharp_plugin gives a streaming RPC exactly one client method - the async streaming call - and deliberately
        /// no <c>...Async</c> twin, so asserting one here would be asserting a bug.
        /// </summary>
        [Fact]
        public void TheLogsStreamingRpcsAreGeneratedAsServerStreamingCalls()
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

            Assert.Equal(MethodType.ServerStreaming, MethodTypeOf(typeof(Logs), "ExportCallLogs"));
            Assert.Contains("ExportCallLogs", clientMethods);
            Assert.DoesNotContain("ExportCallLogsAsync", clientMethods);

            foreach (string rpc in LogsRpcs.Except(LogsStreamingRpcs))
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
                         "ondewo.vtsi.Softphones", "ondewo.vtsi.Campaigns", "ondewo.vtsi.Events",
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

        /// <summary>
        /// Every service the VTSI protos declare is generated - nothing more, nothing less - with exactly
        /// the declared RPCs, in declaration order, each with its declared streaming shape, and a client
        /// that exposes it: a unary RPC as a blocking and an <c>...Async</c> method, a streaming RPC as one.
        /// Derived from the protos (or the committed stubs), so a new RPC needs no test edit.
        /// </summary>
        [Fact]
        public void EveryServiceTheVtsiApiDeclaresIsGeneratedWithEveryRpcAndItsStreamingShape()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);
            Type[] serviceClasses = ProductStubs.Assembly.GetTypes()
                .Where(type => type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.PropertyType == typeof(ServiceDescriptor))
                .Where(type => ServiceDescriptorOf(type).FullName.StartsWith(VtsiApiSurface.Package + ".", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(
                VtsiApiSurface.Services.Keys.OrderBy(name => name, StringComparer.Ordinal),
                serviceClasses.Select(type => ServiceDescriptorOf(type).FullName).OrderBy(name => name, StringComparer.Ordinal));

            foreach (Type serviceClass in serviceClasses)
            {
                ServiceDescriptor service = ServiceDescriptorOf(serviceClass);
                IReadOnlyList<VtsiApiSurface.Rpc> declared = VtsiApiSurface.RpcsOf(service.FullName);
                Assert.Equal(declared.Select(rpc => rpc.Name), service.Methods.Select(method => method.Name));

                Type clientClass = serviceClass.GetNestedType(serviceClass.Name + "Client");
                Assert.NotNull(clientClass);
                Assert.NotNull(Activator.CreateInstance(clientClass, channel));
                string[] clientMethods = clientClass.GetMethods().Select(method => method.Name).Distinct().ToArray();

                foreach (VtsiApiSurface.Rpc rpc in declared)
                {
                    MethodDescriptor method = service.FindMethodByName(rpc.Name);
                    Assert.Equal(rpc.ClientStreaming, method.IsClientStreaming);
                    Assert.Equal(rpc.ServerStreaming, method.IsServerStreaming);
                    MethodType expected = (rpc.ClientStreaming, rpc.ServerStreaming) switch
                    {
                        (false, false) => MethodType.Unary,
                        (false, true) => MethodType.ServerStreaming,
                        (true, false) => MethodType.ClientStreaming,
                        _ => MethodType.DuplexStreaming,
                    };
                    Assert.Equal(expected, MethodTypeOf(serviceClass, rpc.Name));
                    Assert.Contains(rpc.Name, clientMethods);
                    if (expected == MethodType.Unary)
                    {
                        Assert.Contains(rpc.Name + "Async", clientMethods);
                    }
                    else
                    {
                        Assert.DoesNotContain(rpc.Name + "Async", clientMethods);
                    }
                }
            }
        }

        /// <summary>
        /// The derivation itself is pinned, so the test above cannot pass by deriving nothing: both
        /// parsers read real declarations, every service the product is known for is found, and CI -
        /// which checks the api submodule out - really reads the protos rather than falling back.
        /// </summary>
        [Fact]
        public void TheDerivedApiSurfaceReadsRealDeclarations()
        {
            Dictionary<string, List<VtsiApiSurface.Rpc>> proto = VtsiApiSurface.ParseProto(
                "syntax = \"proto3\";\npackage ondewo.vtsi;\n// rpc Commented (A) returns (B);\n" +
                "service S {\n  rpc One (A) returns (B) { option (x) = { get: \"//v1/{name}\" }; }\n" +
                "  rpc Two(stream A)\n    returns (stream B);\n  rpc Three (A) returns (stream B);\n}\n");
            Assert.Equal(
                new[] { new VtsiApiSurface.Rpc("One", false, false), new VtsiApiSurface.Rpc("Two", true, true), new VtsiApiSurface.Rpc("Three", false, true) },
                Assert.Single(proto, entry => entry.Key == "ondewo.vtsi.S").Value);

            Dictionary<string, List<VtsiApiSurface.Rpc>> stub = VtsiApiSurface.ParseStub(
                File.ReadAllText(Path.Combine(VtsiApiSurface.RepositoryRoot, "api", "Ondewo", "Vtsi", "LogsGrpc.cs")));
            Assert.Contains(new VtsiApiSurface.Rpc("StreamCallLogs", false, true), stub["ondewo.vtsi.Logs"]);
            Assert.Contains(new VtsiApiSurface.Rpc("ListCallLogs", false, false), stub["ondewo.vtsi.Logs"]);

            foreach (string service in new[]
                     {
                         "ondewo.vtsi.Calls", "ondewo.vtsi.Projects", "ondewo.vtsi.Logs",
                         "ondewo.vtsi.Softphones", "ondewo.vtsi.Campaigns", "ondewo.vtsi.Events",
                     })
            {
                Assert.Contains(service, VtsiApiSurface.Services.Keys);
            }

            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.Equal("protos", VtsiApiSurface.Source);
            }
        }

        /// <summary>Asserts <paramref name="pins"/> all occur in <paramref name="actual"/>, in the same relative order.</summary>
        private static void AssertContainsInOrder(IEnumerable<string> pins, IEnumerable<string> actual)
        {
            List<string> names = actual.ToList();
            int previous = -1;
            foreach (string pin in pins)
            {
                int index = names.IndexOf(pin);
                Assert.True(index >= 0, $"{pin} is not declared");
                Assert.True(index > previous, $"{pin} is declared out of order");
                previous = index;
            }
        }

        private static ServiceDescriptor ServiceDescriptorOf(Type serviceClass) =>
            (ServiceDescriptor)serviceClass.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

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
