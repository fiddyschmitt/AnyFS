using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>An answer's JSON read from its bytes (<see cref="JsonBody"/>), as the clients that read it through a string did.</summary>
[TestClass]
public class JsonBodyTests
{
    /// <summary>A body that comes as the network gives it: its length not known beforehand, and read once only until it is held whole.</summary>
    private sealed class Arriving(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [TestMethod]
    public async Task TheJsonIsReadWhateverItsSize()
    {
        var big = "{\"value\":[" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"{{\"name\":\"file {i}.docx\",\"size\":{i}}}")) + "]}";
        foreach (var content in new HttpContent[] { new StringContent(big, Encoding.UTF8, "application/json"), new Arriving(Encoding.UTF8.GetBytes(big)) })
        {
            using var doc = await JsonBody.ReadAsync(content, CancellationToken.None);
            Assert.AreEqual(5000, doc.RootElement.GetProperty("value").GetArrayLength());
            Assert.AreEqual("file 4999.docx", doc.RootElement.GetProperty("value")[4999].GetProperty("name").GetString());
        }
    }

    [TestMethod]
    public async Task AnAnswerWithNothingInItIsAnEmptyObject()
    {
        foreach (var content in new HttpContent[] { new ByteArrayContent([]), new Arriving([]) })
        {
            using var doc = await JsonBody.ReadAsync(content, CancellationToken.None);
            Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.AreEqual(0, doc.RootElement.EnumerateObject().Count());
        }
    }

    [TestMethod]
    public async Task ABodyThatIsNotJsonCanStillBeReadForAnErrorsWords()
    {
        // a sign-in page in the service's place: the caller says so with the page's title, which it reads from the same answer
        var content = new Arriving(Encoding.UTF8.GetBytes("<html><title>Sign in</title></html>"));
        await Assert.ThrowsAsync<JsonException>(() => JsonBody.ReadAsync(content, CancellationToken.None));
        StringAssert.Contains(await content.ReadAsStringAsync(), "<title>Sign in</title>");
    }
}
