using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BaGet.Protocol;
using BaGet.Protocol.Models;
using Newtonsoft.Json;
using NuGet.Packaging;

namespace FindCommandLineApps
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            ThreadPool.SetMinThreads(32, completionPortThreads: 4);
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 10000;

            var serializer = new JsonSerializer();
            var client = new NuGetClient("https://api.nuget.org/v3/index.json");
            var channel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(capacity: 1000)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var producerTask = ProduceWorkAsync(serializer, channel.Writer);
            var workerTasks = Enumerable
                .Range(0, 32)
                .Select(x => ConsumeWork(client, serializer, channel.Reader));

            await Task.WhenAll(workerTasks.Append(producerTask));

            Console.WriteLine("Done");
        }

        private static async Task ProduceWorkAsync(JsonSerializer serializer, ChannelWriter<SearchResult> channel)
        {
            var httpClient = new HttpClient();

            var done = false;
            var skip = 0;
            var take = 1000;

            do
            {
                var url = $"https://azuresearch-usnc.nuget.org/query?packageType=Dotnettool&skip={skip}&take={take}";
            
                using var response = await httpClient.GetAsync(url);
                using var stream = await response.Content.ReadAsStreamAsync();
                using var textReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(textReader);

                var results = serializer.Deserialize<SearchResponse>(jsonReader);

                foreach (var result in results.Data)
                {
                    await channel.WriteAsync(result);
                }

                done = !results.Data.Any();
                skip += take;
            }
            while (done == false);

            channel.Complete();
        }

        private static async Task ConsumeWork(
            NuGetClient client,
            JsonSerializer serializer,
            ChannelReader<SearchResult> channel)
        {
            await Task.Yield();

            while (await channel.WaitToReadAsync())
            {
                while (channel.TryRead(out var item))
                {
                    // TODO: Likely not seekable.
                    var packageId = item.PackageId;
                    var packageVersion = item.ParseVersion();

                    using var packageStream = await client.GetPackageStreamAsync(packageId, packageVersion);
                    using var packageReader = new PackageArchiveReader(packageStream);

                    var depsFiles = packageReader
                        .GetFiles()
                        .Where(f => f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase));

                    foreach (var depsFile in depsFiles)
                    {
                        using var stream = packageReader.GetStream(depsFile);
                        using var textReader = new StreamReader(stream);
                        using var jsonReader = new JsonTextReader(textReader);

                        var deps = serializer.Deserialize<DepsModel>(jsonReader);
                        var match = deps.Libraries.Keys.Any(
                            l => l.StartsWith("System.CommandLine", StringComparison.OrdinalIgnoreCase));

                        if (match)
                        {
                            Console.WriteLine($"{packageId} uses System.CommandLine");
                            return;
                        }
                    }

                }
            }
        }
    }

    public class DepsModel
    {
        [JsonProperty("libraries")]
        public Dictionary<string, object> Libraries { get; set; }    
    }
}
