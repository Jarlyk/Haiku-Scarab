using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Scarab.Interfaces;
using Scarab.Models;

namespace Scarab.Services
{
    public class ModDatabase : IModDatabase
    {
        private const string MODLINKS_URI = "https://raw.githubusercontent.com/Schyvun/Haiku-Modlinks/main/ModLinks.xml";
        
        private const string FALLBACK_MODLINKS_URI = "https://cdn.jsdelivr.net/gh/hk-modding/modlinks@latest/ModLinks.xml";

        public (string Url, int Version, string SHA256) Api { get; }

        public IEnumerable<ModItem> Items => _items;

        private readonly List<ModItem> _items = new();

        public ModDatabase(IModSource mods, ModLinks ml)
        {
            foreach (var mod in ml.Manifests)
            {
                var item = new ModItem
                (
                    link: mod.Links.OSUrl,
                    version: mod.Version.Value,
                    name: mod.Name,
                    shasum: mod.Links.SHA256,
                    description: mod.Description,
                    repository: mod.Repository,
                    dependencies: mod.Dependencies,
                    
                    state: mods.FromManifest(mod)
                );
                
                _items.Add(item);
            }

            _items.Sort((a, b) => string.Compare(a.Name, b.Name));
        }

        public ModDatabase(IModSource mods, string modlinks) : this(mods, FromString<ModLinks>(modlinks)) { }
        
        public static async Task<ModLinks> FetchContent()
        {
            using var hc = new HttpClient {
                DefaultRequestHeaders = {
                    CacheControl = new CacheControlHeaderValue {
                        NoCache = true,
                        MustRevalidate = true
                    }
                }
            };
            
            Task<ModLinks> ml = FetchModLinks(hc);

            return await ml;
        }

        private static T FromString<T>(string xml)
        {
            var serializer = new XmlSerializer(typeof(T));
            
            using TextReader reader = new StringReader(xml);

            var obj = (T?) serializer.Deserialize(reader);

            if (obj is null)
                throw new InvalidDataException();

            return obj;
        }
        
        private static async Task<ModLinks> FetchModLinks(HttpClient hc)
        {
            return FromString<ModLinks>(await FetchWithFallback(hc, new Uri(MODLINKS_URI), new Uri(FALLBACK_MODLINKS_URI)));
        }

        private static async Task<string> FetchWithFallback(HttpClient hc, Uri uri, Uri fallback)
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                return await hc.GetStringAsync(uri, cts.Token);
            }
            catch (Exception e) when (e is TaskCanceledException or HttpRequestException)
            {
                var cts = new CancellationTokenSource(10000);
                return await hc.GetStringAsync(fallback, cts.Token);
            }
        }
    }
}