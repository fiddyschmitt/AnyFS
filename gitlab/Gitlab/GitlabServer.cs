using gitlab.Gitlab.Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace gitlab.Gitlab
{
    public class GitlabServer
    {
        public GitlabServer(string serverBaseUrl, string privateToken)
        {
            ServerBaseUrl = serverBaseUrl;
            PrivateToken = privateToken;
        }

        public string ServerBaseUrl { get; }
        public string PrivateToken { get; }

        public IEnumerable<Project> GetProjects()
        {
            var url = @$"{ServerBaseUrl}/api/v4/projects?simple=true&pagination=keyset&per_page=100&order_by=id&sort=asc";

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", PrivateToken);

            var sw = new Stopwatch();
            while (true)
            {
                sw.Restart();

                var response = httpClient.GetAsync(url).Result;

                var responseStr = response.Content.ReadAsStringAsync().Result;

                sw.Stop();
                Console.WriteLine($"{sw.ElapsedMilliseconds:N0} ms");

                //Console.WriteLine(responseStr);

                var projectsDyn = JsonConvert.DeserializeObject<dynamic[]>(responseStr);
                if (projectsDyn == null) break;

                var result = projectsDyn
                                .Select(proj => new Project()
                                {
                                    CreatedUTC = proj.Value<DateTime>("created_at"),
                                    UpdatedUTC = proj.Value<DateTime>("last_activity_at"),
                                    Description = proj.description,
                                    Id = proj.id,
                                    Name = proj.name,
                                    Path = proj.path,
                                    Namespace = proj["namespace"].name,
                                    NamespacePath = proj["namespace"].path,

                                })
                                .ToList();

                foreach (var proj in result)
                {
                    yield return proj;
                }

                if (response.Headers.TryGetValues("link", out var links))
                {
                    //Console.WriteLine($"Next page link: {links.First()}");
                    url = links.First().Split(["<", ">"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }
                else
                {
                    break;
                }
            }
        }
    }
}
