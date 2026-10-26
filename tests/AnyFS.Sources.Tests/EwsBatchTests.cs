using System.Net;
using System.Text;
using AnyFS.Sources.Outlook.Ews;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Several items, and several attachments, asked for in one request (GetItem and GetAttachment take many ids), over the
/// fake's Outlook on the web and its Exchange server; and asked for one at a time where a service takes one id per
/// request (Microsoft Graph).
/// </summary>
[TestClass]
public class EwsBatchTests
{
    [TestMethod]
    public async Task SeveralItemsAndTheirAttachmentsComeInOneRequestEach()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        var when = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var one = fake.AddMessage(inbox, "One", ("Adele Vance", "adelev@fake.test"), when, "<p>One.</p>", [new FakeExchange.Attachment("one.txt", "text/plain", Encoding.ASCII.GetBytes("first"), false, null)]);
        var two = fake.AddMessage(inbox, "Two", ("Alex Wilber", "alexw@fake.test"), when.AddHours(1), "<p>Two.</p>", [new FakeExchange.Attachment("two.txt", "text/plain", Encoding.ASCII.GetBytes("second!"), false, null)]);
        using var web = new OwaJsonService(fake.Origin, FakeExchange.Account, (_, _) => Task.FromResult(fake.ServerToken), fake.Handler(), NullLogger.Instance);
        using var server = new SoapEwsService(new Uri(fake.Origin, "/EWS/Exchange.asmx"), CredentialCache.DefaultNetworkCredentials, false, fake.Handler(), "batch|test", NullLogger.Instance);
        foreach (IEwsService service in new IEwsService[] { web, server })
        {
            var what = service.GetType().Name;
            fake.Actions.Clear();
            var items = await service.GetItemsAsync([one.Id, "gone", two.Id], includeMime: true, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "One", null, "Two" }, items.Select(i => i?.Subject).ToList(), $"{what}: in the order asked, one gone");
            Assert.IsNotNull(items[0]!.Mime, what);
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetItem"), $"{what}: one request");

            var attachments = await service.GetAttachmentsAsync([items[2]!.Attachments.Single().Id, "gone", items[0]!.Attachments.Single().Id], CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "second!", null, "first" }, attachments.Select(a => a?.Content is { } c ? Encoding.ASCII.GetString(c) : null).ToList(), what);
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetAttachment"), $"{what}: one request");
            Assert.AreEqual(0, (await service.GetItemsAsync([], includeMime: true, CancellationToken.None)).Count);
        }

        Assert.AreEqual(6, fake.ItemsFetched, "three ids twice");
    }

    [TestMethod]
    public async Task AServiceThatTakesOneIdIsAskedForEachAloneAndARefusalIsNull()
    {
        var asked = new List<string>();
        var got = await EwsBatch.OneByOneAsync<string>(["a", "refused", "c"], id =>
        {
            lock (asked)
            {
                asked.Add(id);
            }

            return id == "refused" ? throw new EwsException("ErrorAccessDenied", "no") : Task.FromResult<string?>(id.ToUpperInvariant());
        });
        CollectionAssert.AreEqual(new[] { "A", null, "C" }, got.ToList());
        Assert.AreEqual(3, asked.Count);

        // trouble with the service itself is the batch's
        await Assert.ThrowsExactlyAsync<EwsException>(() => EwsBatch.OneByOneAsync<string>(["a"], _ => throw new EwsException("401", "signed out")));
        await Assert.ThrowsExactlyAsync<EwsException>(() => EwsBatch.OneByOneAsync<string>(["a"], _ => throw new EwsException("503", "busy", transient: true)));
    }
}
