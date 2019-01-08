using Kv6Receiver.bison;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Kv6Receiver
{
    class Program
    {
        static XmlSerializer serializer = new XmlSerializer(typeof(VV_TM_PUSH));
        static HttpClient httpClient = new HttpClient();
        static TelemetryClient telemetryClient = null;
        static DependencyTrackingTelemetryModule depTrackingModule = null;

        static void Main(string[] args)
        {
            httpClient.BaseAddress = new Uri("http://localhost:9200/qbuzz/");
            var nodeAddr = "tcp://pubsub.besteffort.ndovloket.nl:7658";
            var subscriptions = new string[] { @"/QBUZZ/KV6posinfo" };
            var instrumentationKey = args.Length > 0 ? args[0] : default(string);

            if (instrumentationKey != null)
            {
                TelemetryConfiguration.Active.InstrumentationKey = instrumentationKey;
                // stamps telemetry with correlation identifiers
                TelemetryConfiguration.Active.TelemetryInitializers.Add(new OperationCorrelationTelemetryInitializer());
                // ensures proper DependencyTelemetry.Type is set for Azure RESTful API calls
                TelemetryConfiguration.Active.TelemetryInitializers.Add(new HttpDependenciesParsingTelemetryInitializer());

                telemetryClient = new TelemetryClient();

                depTrackingModule = new DependencyTrackingTelemetryModule();
                //module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.windows.net");
                depTrackingModule.Initialize(TelemetryConfiguration.Active);

            }

            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            var processor = StartProcessor(nodeAddr, subscriptions, cancellationTokenSource);

            Console.WriteLine("Processor running. Press Ctrl-C to exit...");
            try
            {
                processor.GetAwaiter().GetResult();
                Console.WriteLine("Processor completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Processor failed: {0}", ex);
            }

            if (telemetryClient != null)
            {
                Console.Write("Shutting down, please wait...");
                telemetryClient.Flush();
                Task.Delay(2000).Wait(); // Flush is not blocking :-s
                depTrackingModule.Dispose();
                Console.WriteLine("Done");
            }
        }

        private static Task StartProcessor(string nodeAddr, string[] subscriptions, CancellationTokenSource cancellationTokenSource)
        {
            telemetryClient.TrackTrace($"Starting processor for {nodeAddr}", SeverityLevel.Information, new Dictionary<string, string>
                {
                    { nameof(nodeAddr), nodeAddr },
                });
            return Task.Factory.StartNew(async () =>
            {
                string index = "kv6";
                using (var socket = new SubscriberSocket())
                {
                    socket.Connect(nodeAddr);
                    try
                    {
                        subscriptions.All((s) =>
                        {
                            socket.Subscribe(s);
                            return true;
                        });

                        while (!cancellationTokenSource.IsCancellationRequested)
                        {
                            var messageList = socket.ReceiveMultipartBytes(2);
                            var subscription = Encoding.UTF8.GetString(messageList[0]);

                            string payload = null;
                            using (GZipStream stream = new GZipStream(new MemoryStream(messageList[1]), CompressionMode.Decompress))
                            using (var sr = new StreamReader(stream))
                                payload = await sr.ReadToEndAsync();

#if DEBUG
                            Console.Write($"{subscription} - {payload.Length} chars...");
#endif

                            var xml = DeserializeMessage(payload);
                            telemetryClient.TrackTrace($"Received payload for {subscription}, size: {payload.Length} characters", SeverityLevel.Verbose, new Dictionary<string, string>
                            {
                                { nameof(subscription), subscription },
                                { nameof(payload), payload },
                            });
                            var jsonObjects = GetJsonRepresentation(xml.Timestamp, xml.KV6posinfo);
                            await StoreJsonObjects(index, jsonObjects);
                        }
                    }
                    finally
                    {
                        socket.Disconnect(nodeAddr);
                        socket.Close();
                        telemetryClient.TrackTrace("Stopped processor", SeverityLevel.Information);
                    }
                }

            }, cancellationTokenSource.Token);
        }

        private static Task StoreJsonObjects(string index, IEnumerable<JObject> jsonObjects)
        {
            jsonObjects.AsParallel().ForAll(async (jObj) =>
            {
                // Guid as ID
                var relativeUri = $"{index}/{Guid.NewGuid()}";
                var stringContent = jObj.ToString();

                try
                {
                    var res = await httpClient.PostAsync(relativeUri, new StringContent(stringContent, Encoding.UTF8, "application/json"));
                    if (!res.IsSuccessStatusCode)
                    {
                        telemetryClient.TrackTrace($"Failed to store json objects, http-status: {res.StatusCode}", SeverityLevel.Error, new Dictionary<string, string>
                            {
                                { "httpstatus", res.StatusCode.ToString() },
                            });
                        Console.WriteLine("Write failure!");
                    }
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(ex);
                    Console.WriteLine("Write failure!");
                }
            });

            return Task.CompletedTask;
        }

        private static IEnumerable<JObject> GetJsonRepresentation(DateTime received, KV6posinfoType[] kV6posinfos)
        {
            /*
                    [System.Xml.Serialization.XmlElementAttribute("ARRIVAL", typeof(ARRIVALType))]
        [System.Xml.Serialization.XmlElementAttribute("DELAY", typeof(DELAYType))]
        [System.Xml.Serialization.XmlElementAttribute("DEPARTURE", typeof(DEPARTUREType))]
        [System.Xml.Serialization.XmlElementAttribute("END", typeof(ENDType))]
        [System.Xml.Serialization.XmlElementAttribute("INIT", typeof(INITType))]
        [System.Xml.Serialization.XmlElementAttribute("OFFROUTE", typeof(OFFROUTEType))]
        [System.Xml.Serialization.XmlElementAttribute("ONPATH", typeof(ONPATHType))]
        [System.Xml.Serialization.XmlElementAttribute("ONROUTE", typeof(ONROUTEType))]
        [System.Xml.Serialization.XmlElementAttribute("ONSTOP", typeof(ONSTOPType))]
        */
            foreach (var kv6obj in kV6posinfos.SelectMany(kv6pi => kv6pi.Items))
            {
                JObject jObj = null;
                string typeName = null;

                if (kv6obj is ARRIVALType kv6Arrival)
                {
                    jObj = JObject.FromObject(kv6Arrival);
                    typeName = "ARRIVAL";
                }
                else if (kv6obj is DELAYType kv6Delay)
                {
                    jObj = JObject.FromObject(kv6Delay);
                    typeName = "DELAY";
                }
                else if (kv6obj is DEPARTUREType kv6Departure)
                {
                    jObj = JObject.FromObject(kv6Departure);
                    typeName = "DEPARTURE";
                }
                else if (kv6obj is ENDType kv6End)
                {
                    jObj = JObject.FromObject(kv6End);
                    typeName = "END";
                }
                else if (kv6obj is INITType kv6Init)
                {
                    jObj = JObject.FromObject(kv6Init);
                    typeName = "INIT";
                }
                else if (kv6obj is OFFROUTEType kv6Offroute)
                {
                    jObj = JObject.FromObject(kv6Offroute);
                    typeName = "OFFROUTE";
                }
                else if (kv6obj is ONPATHType kv6Onpath)
                {
                    jObj = JObject.FromObject(kv6Onpath);
                    typeName = "ONPATH";
                }
                else if (kv6obj is ONROUTEType kv6Onroute)
                {
                    jObj = JObject.FromObject(kv6Onroute);
                    typeName = "ONROUTE";
                }
                else if (kv6obj is ONSTOPType kv6Onstop)
                {
                    jObj = JObject.FromObject(kv6Onstop);
                    typeName = "ONSTOP";
                }
                else
                    throw new ArgumentException($"Unexpected itemtype ({kv6obj.GetType().Name})", nameof(kV6posinfos));

                jObj.Add("type", JToken.FromObject(typeName));
                jObj.Add("received", JToken.FromObject(received));
                yield return jObj;
            }
        }

        private static VV_TM_PUSH DeserializeMessage(string msg)
        {
            using (var sr = new StringReader(msg))
            {
                return (VV_TM_PUSH)serializer.Deserialize(sr);
            }
        }
    }
}
