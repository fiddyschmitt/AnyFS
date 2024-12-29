using gitlab.Gitlab;
using libCommon;

namespace gitlab
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var gitlabServer = new GitlabServer("https://gitserver", "api-key-123456");

            var projects = gitlabServer.GetProjects().ToList();
            var projectsStr = projects.ToJson(true);
        }
    }
}
