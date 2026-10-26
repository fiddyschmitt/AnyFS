using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Address books (spec 7.7): the organisation's address lists through the web session's People (GetPeopleFilters,
/// FindPeople), served as "Address Books\(list)\(name).vcf" beside the mailbox's folders, over the fake's directory.
/// </summary>
[TestClass]
public class OutlookAddressBookTests
{
    private const string Gal = "Address Books/Default Global Address List";

    private static OutlookSettings Settings() => new() { Path = FakeExchange.Account, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };

    private static async Task<OutlookSource> StartAsync(FakeExchange fake, TestSourceContext context)
    {
        var source = new OutlookSource(Settings(), fake.StandIn()) { DirectoryColdPause = TimeSpan.Zero };
        await source.InitialiseAsync(context, CancellationToken.None);
        return source;
    }

    /// <summary>A directory as the sandbox's: a global list with people, a group and two of one name; rooms; an empty list.</summary>
    private static FakeExchange WithDirectory()
    {
        var fake = FakeExchange.Mailbox();
        var gal = fake.AddAddressList("Default Global Address List");
        gal.People.Add(FakeExchange.Person("Adele Vance", "AdeleV@fake.test", "Adele", "Vance", "Retail Manager", "Retail", "Contoso", "18/2111", "Tulsa", "2 Main St"));
        gal.People.Add(FakeExchange.Person("Alex Wilber", "AlexW@fake.test", "Alex", "Wilber", "Marketing Assistant", "Marketing", city: "Seattle"));
        gal.People.Add(FakeExchange.Person("Alex Wilber", "AlexW2@fake.test", "Alex", "Wilber", "Engineer", "Engineering"));
        gal.People.Add(FakeExchange.Person("All Company", "allcompany@fake.test"));
        fake.AddAddressList("All Rooms").People.Add(FakeExchange.Person("Conf Room Adams", "Adams@fake.test", office: "Building 1"));
        fake.AddAddressList("All Contacts");
        return fake;
    }

    [TestMethod]
    public async Task TheOrganisationsAddressListsAreFoldersOfCards()
    {
        var fake = WithDirectory();
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetPeopleFilters"), "the lists are asked for at the start");
            Assert.AreEqual(0, fake.Actions.Count(a => a == "FindPeople"), "no list is read before its folder is opened");
            CollectionAssert.Contains((await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList(), "Address Books");
            CollectionAssert.AreEqual(new[] { "Default Global Address List", "All Rooms", "All Contacts" }, (await OutlookFixtures.ListAsync(source, "Address Books")).Select(e => e.Name).ToList(),
                "the organisation's lists in the server's order; the mailbox's own contact folders are its folders");

            var gal = await OutlookFixtures.ListAsync(source, Gal);
            CollectionAssert.AreEqual(new[] { "Adele Vance.vcf", "Alex Wilber.vcf", "Alex Wilber (2).vcf", "All Company.vcf" }, gal.Select(e => e.Name).ToList());
            Assert.AreEqual(1, fake.Actions.Count(a => a == "FindPeople"));

            var card = await OutlookFixtures.TextAsync(source, Gal + "/Adele Vance.vcf");
            Assert.AreEqual(gal[0].Length, Encoding.UTF8.GetByteCount(card), "the listing gives the card's length");
            foreach (var line in new[] { "FN:Adele Vance", "N:Vance;Adele;;;", "ORG:Contoso;Retail", "TITLE:Retail Manager", "EMAIL;TYPE=INTERNET,PREF:AdeleV@fake.test", "ADR;TYPE=WORK:;18/2111;2 Main St;Tulsa;;;", "X-MS-IMADDRESS:sip:adelev@fake.test" })
            {
                StringAssert.Contains(card, line + "\r\n");
            }

            Assert.IsFalse(card.Contains("/o=ExchangeLabs", StringComparison.Ordinal), "an X.500 address opens nothing outside Exchange");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, Gal + "/Alex Wilber (2).vcf"), "EMAIL;TYPE=INTERNET,PREF:AlexW2@fake.test");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, Gal + "/All Company.vcf"), "FN:All Company");
            Assert.IsNotNull(gal[0].Modified, "a card is dated by when it was read as it is");

            Assert.IsNull(await source.StatAsync("Address Books/All Rooms/desktop.ini", CancellationToken.None), "a name no card has is not looked for");
            Assert.AreEqual(1, fake.Actions.Count(a => a == "FindPeople"), "the rooms are not read for it, nor the global list again");
            await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("Address Books/All Rooms", VfsOpenMode.Read, CancellationToken.None));
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, "Address Books/All Rooms/Conf Room Adams.vcf"), "ADR;TYPE=WORK:;Building 1;;;;;");
            Assert.AreEqual(0, (await OutlookFixtures.ListAsync(source, "Address Books/All Contacts")).Count);
            Assert.AreEqual(source.Tree.FileCount + 5, source.Counters.FilesKnown, "the entries read are counted");
        }
    }

    [TestMethod]
    public async Task ABigListIsReadAPageAtATimeAndAColdOneTwice()
    {
        var fake = FakeExchange.Mailbox();
        var users = fake.AddAddressList("All Users");
        for (var i = 1; i <= 1234; i++)
        {
            users.People.Add(FakeExchange.Person($"Person {i:D4}", $"person{i}@fake.test"));
        }

        fake.MostPeoplePerPage = 200;
        fake.ColdPeopleAnswers = 1;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var names = (await OutlookFixtures.ListAsync(source, "Address Books/All Users")).Select(e => e.Name).ToList();
            Assert.AreEqual(1234, names.Count, "every page, though the server gave fewer than asked for");
            Assert.AreEqual("Person 1234.vcf", names[^1]);
            Assert.AreEqual(1 + 7, fake.Actions.Count(a => a == "FindPeople"), "the cold answer asked once more, then seven pages of 200");
        }
    }

    [TestMethod]
    public async Task AServerWithoutListsShowsNoFolderUntilARefreshFindsThem()
    {
        var fake = FakeExchange.Mailbox();
        fake.Intercept = (_, action) => action == "GetPeopleFilters"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"Body":{"ErrorCode":500,"FaultMessage":"The directory is not there","ResponseCode":"ErrorInternalServerError"}}""", Encoding.UTF8, "application/json") }
            : null;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            Assert.IsFalse((await OutlookFixtures.ListAsync(source, "")).Any(e => e.Name == "Address Books"), "a failure leaves the folder out; the mailbox is served");
            Assert.IsNull(await source.StatAsync("Address Books", CancellationToken.None));

            fake.Intercept = null;
            await source.RefreshAsync(CancellationToken.None);
            Assert.IsFalse((await OutlookFixtures.ListAsync(source, "")).Any(e => e.Name == "Address Books"), "a server that gives no lists: no folder");

            fake.AddAddressList("Default Global Address List").People.Add(FakeExchange.Person("Adele Vance", "AdeleV@fake.test"));
            await source.RefreshAsync(CancellationToken.None);
            Assert.IsFalse((await OutlookFixtures.ListAsync(source, "")).Any(e => e.Name == "Address Books"), "lists that were read are asked for again only when a day old");
        }

        using var again = new TestSourceContext("outlook");
        var restarted = await StartAsync(fake, again);
        await using (restarted)
        {
            CollectionAssert.AreEqual(new[] { "Adele Vance.vcf" }, (await OutlookFixtures.ListAsync(restarted, Gal)).Select(e => e.Name).ToList());
        }
    }

    [TestMethod]
    public async Task ARestartServesTheKeptAddressBooksAndReadsADayOldListAgainBehind()
    {
        var fake = WithDirectory();
        byte[] kept;
        DateTimeOffset? alexRead;
        using (var first = new TestSourceContext("outlook"))
        {
            var source = await StartAsync(fake, first);
            await using (source)
            {
                alexRead = (await OutlookFixtures.ListAsync(source, Gal)).Single(e => e.Name == "Alex Wilber.vcf").Modified;
            }

            kept = await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, AddressBookIndexFile.FileName));
        }

        fake.Actions.Clear();
        using (var second = new TestSourceContext("outlook"))
        {
            await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, AddressBookIndexFile.FileName), kept);
            var source = await StartAsync(fake, second);
            await using (source)
            {
                Assert.AreEqual(4, (await OutlookFixtures.ListAsync(source, Gal)).Count);
                Assert.IsFalse(fake.Actions.Contains("GetPeopleFilters") || fake.Actions.Contains("FindPeople"), "served as kept: " + string.Join(",", fake.Actions));
            }
        }

        // two days on, and Adele has a new title: served as kept at once and read again behind; an entry as it was keeps its date
        fake.AddressList("Default Global Address List").People[0]["Title"] = "Retail Director";
        fake.Actions.Clear();
        using (var third = new TestSourceContext("outlook"))
        {
            var path = Path.Combine(third.StateDirectory, AddressBookIndexFile.FileName);
            await File.WriteAllBytesAsync(path, kept);
            var index = AddressBookIndexFile.Load(path, FakeExchange.Account) ?? throw new AssertFailedException("the kept file reads");
            AddressBookIndexFile.Save(path, index with { ListsTaken = index.ListsTaken.AddDays(-2), Members = [.. index.Members.Select(m => m with { Taken = m.Taken.AddDays(-2) })] });
            var source = await StartAsync(fake, third);
            await using (source)
            {
                Assert.AreEqual(4, (await OutlookFixtures.ListAsync(source, Gal)).Count, "served as kept at once");
                var renewed = false;
                for (var i = 0; i < 100 && !renewed; i++)
                {
                    await Task.Delay(100);
                    renewed = (await OutlookFixtures.TextAsync(source, Gal + "/Adele Vance.vcf")).Contains("TITLE:Retail Director", StringComparison.Ordinal);
                }

                Assert.IsTrue(renewed, "read again behind");
                Assert.AreEqual(1, fake.Actions.Count(a => a == "GetPeopleFilters"), "the day-old lists asked for again");
                var after = await OutlookFixtures.ListAsync(source, Gal);
                Assert.AreEqual(alexRead, after.Single(e => e.Name == "Alex Wilber.vcf").Modified, "an entry as it was keeps its date");
                Assert.IsTrue(after.Single(e => e.Name == "Adele Vance.vcf").Modified > alexRead, "a changed one is dated anew");
            }
        }
    }

    [TestMethod]
    public async Task AServerThatTakesOnlyTheDefaultShapeStillGivesCards()
    {
        var fake = WithDirectory();
        fake.RefuseFullerPersonaShape = true;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var card = await OutlookFixtures.TextAsync(source, Gal + "/Adele Vance.vcf");
            StringAssert.Contains(card, "ORG:Contoso\r\n", "the default shape's company");
            StringAssert.Contains(card, "EMAIL;TYPE=INTERNET,PREF:AdeleV@fake.test\r\n");
            Assert.IsFalse(card.Contains("TITLE:", StringComparison.Ordinal), "no title without the fields asked for beside the default ones");
            Assert.AreEqual(1 + 5 + 1, fake.Actions.Count(a => a == "FindPeople"), "refused once, each field alone, then the default shape");
            await OutlookFixtures.ListAsync(source, "Address Books/All Rooms");
            Assert.AreEqual(8, fake.Actions.Count(a => a == "FindPeople"), "the default shape alone from then on");
        }
    }

    [TestMethod]
    public async Task AServerThatRefusesOneCardFieldStillGivesTheOthers()
    {
        // a server that takes fewer of the fields than Exchange Online did on 26-Oct-2026
        var fake = WithDirectory();
        fake.RefusedPersonaFields.Add("persona:OfficeLocations");
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var card = await OutlookFixtures.TextAsync(source, Gal + "/Adele Vance.vcf");
            foreach (var line in new[] { "TITLE:Retail Manager", "ORG:Contoso;Retail", "ADR;TYPE=WORK:;;2 Main St;Tulsa;;;", "X-MS-IMADDRESS:sip:adelev@fake.test" })
            {
                StringAssert.Contains(card, line + "\r\n", "a field the server takes");
            }

            Assert.IsFalse(card.Contains("18/2111", StringComparison.Ordinal), "the office, which the server refuses, is not asked for");
            Assert.AreEqual(1 + 5 + 1, fake.Actions.Count(a => a == "FindPeople"), "refused once, each field alone, then the page with those taken");
            await OutlookFixtures.ListAsync(source, "Address Books/All Rooms");
            Assert.AreEqual(8, fake.Actions.Count(a => a == "FindPeople"), "the fields taken asked for from then on, in one request");
        }
    }

    [TestMethod]
    public void ExchangeOnlinesPersonasAreReadWithTheirArrayNamedFields()
    {
        // a persona as Exchange Online's FindPeople gave it on 26-Oct-2026 (the sandbox's address made the fake's)
        using var online = JsonDocument.Parse("""
            {"PersonaId":{"Id":"AAUQAKMDFRyboZRMofTcwQ7bVy0="},"PersonaTypeString":"Person","CreationTimeString":"0001-01-02T00:00:00Z","DisplayName":"Alex Wilber",
             "DisplayNameFirstLast":"Alex Wilber","DisplayNameLastFirst":"Alex Wilber","FileAs":"","GivenName":"Alex","Surname":"Wilber","Title":"Marketing Assistant",
             "EmailAddress":{"Name":"Alex Wilber","EmailAddress":"AlexW@fake.test","RoutingType":"SMTP","MailboxType":"Mailbox"},
             "EmailAddresses":[{"Name":"Alex Wilber","EmailAddress":"AlexW@fake.test","RoutingType":"SMTP","MailboxType":"Mailbox"}],
             "ImAddress":"sip:alexw@fake.test","WorkCity":"San Diego","RelevanceScore":2147483647,
             "OfficeLocationsArray":[{"Value":"131/1104","Attributions":["0"]}],"ImAddressesArray":[{"Value":"sip:alexw@fake.test","Attributions":["0"]}],
             "BusinessAddressesArray":[{"Value":{"Street":"9256 Towne Center Dr., Suite 400","City":"San Diego","State":"CA","Country":"United States","PostalCode":"92121",
               "PostOfficeBox":null,"Type":"Business","LocationUri":"AlexW@fake.test","LocationSource":"Contact"},"Attributions":["0"]}],
             "DepartmentsArray":[{"Value":"Marketing","Attributions":["0"]}],"ADObjectId":"1c1503a3-a19b-4c94-a1f4-dcc10edb572d"}
            """);
        var alex = OwaJsonService.PersonaOf(online.RootElement)!;
        Assert.AreEqual("Person", alex.Kind);
        Assert.AreEqual("Marketing", alex.Department);
        Assert.AreEqual("131/1104", alex.Office);
        CollectionAssert.AreEqual(new[] { new DirectoryAddress("WORK", "9256 Towne Center Dr., Suite 400", "San Diego", "CA", "92121", "United States") }, alex.Addresses.ToList());
        var card = Encoding.UTF8.GetString(Outlook.Files.ContactFile.Build(alex));
        StringAssert.Contains(card, "ORG:;Marketing\r\n");
        StringAssert.Contains(card.Replace("\r\n ", "", StringComparison.Ordinal), """ADR;TYPE=WORK:;131/1104;9256 Towne Center Dr.\, Suite 400;San Diego;CA;92121;United States""" + "\r\n", "the comma escaped, the line unfolded");
    }

    [TestMethod]
    public void APersonasManyValuedFieldsAreReadAsWellAsItsSingleOnes()
    {
        using var many = JsonDocument.Parse("""
            {"PersonaId":{"Id":"AAUQ1"},"DisplayName":"Megan Bowen","Titles":[{"Value":"Marketing Manager","Attributions":["0"]}],"Departments":[{"Value":"Marketing"}],
             "EmailAddresses":[{"Value":{"Name":"Megan Bowen","EmailAddress":"MeganB@fake.test","RoutingType":"SMTP"}}],
             "BusinessPhoneNumbers":[{"Value":{"Number":"+1 412 555 0109","Type":"Business"}}],"HomePhones":[{"Value":{"Number":"+1 412 555 0110"}}],
             "BusinessAddresses":[{"Value":{"Street":"1 Main St","City":"Pittsburgh","State":"PA","PostalCode":"15201","Country":"United States"}}],
             "OfficeLocations":[{"Value":"12/1110"}]}
            """);
        var megan = OwaJsonService.PersonaOf(many.RootElement)!;
        Assert.AreEqual("Megan Bowen", megan.Name);
        Assert.AreEqual("AAUQ1", megan.Id);
        Assert.AreEqual("Marketing Manager", megan.JobTitle);
        Assert.AreEqual("Marketing", megan.Department);
        CollectionAssert.AreEqual(new[] { "MeganB@fake.test" }, megan.Emails.ToList());
        CollectionAssert.AreEqual(new[] { new DirectoryPhone("WORK,VOICE", "+1 412 555 0109"), new DirectoryPhone("HOME,VOICE", "+1 412 555 0110") }, megan.Phones.ToList());
        var card = Encoding.UTF8.GetString(Outlook.Files.ContactFile.Build(megan));
        StringAssert.Contains(card, "ADR;TYPE=WORK:;12/1110;1 Main St;Pittsburgh;PA;15201;United States\r\n", "the office is the work address's extended part");

        using var bare = JsonDocument.Parse("""{"EmailAddress":{"EmailAddress":"room4@fake.test","RoutingType":"SMTP"}}""");
        Assert.AreEqual("room4@fake.test", OwaJsonService.PersonaOf(bare.RootElement)!.Name, "no name: its address");
        using var none = JsonDocument.Parse("""{"EmailAddresses":[{"EmailAddress":"/o=ExchangeLabs/cn=Recipients/cn=x","RoutingType":"EX"}]}""");
        Assert.IsNull(OwaJsonService.PersonaOf(none.RootElement), "neither a name nor an address it can give");
    }
}
