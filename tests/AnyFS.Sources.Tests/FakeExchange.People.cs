using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The fake's directory, as Outlook on the web's People answers it (Spike 11, 24-Oct-2026): GetPeopleFilters (a body
/// of "{}"; a JSON array of filters, the mailbox's own contact folders first, a folder id and a parent each, then the
/// address lists, a GUID and no parent) and FindPeople over an address list, paged, with the total in view. A new
/// fake has no address lists, as a mailbox whose server gives none.
/// </summary>
internal sealed partial class FakeExchange
{
    private readonly List<AddressListFake> _addressLists = [];

    /// <summary>How many FindPeople answers come back empty before the directory answers, as a cold one did once (Spike 11).</summary>
    public int ColdPeopleAnswers { get; set; }

    /// <summary>The most entries a FindPeople answer holds, whatever is asked for (a server's own cap).</summary>
    public int MostPeoplePerPage { get; set; } = 1000;

    /// <summary>An address list of the directory, last of those the People page lists.</summary>
    public AddressListFake AddAddressList(string name)
    {
        lock (_gate)
        {
            var list = new AddressListFake(Guid.NewGuid().ToString(), name);
            _addressLists.Add(list);
            return list;
        }
    }

    /// <summary>The address list of this name.</summary>
    public AddressListFake AddressList(string name)
    {
        lock (_gate)
        {
            return _addressLists.Single(l => l.Name == name);
        }
    }

    /// <summary>
    /// A person as Exchange Online gave one through Outlook on the web (26-Oct-2026): the default shape's fields, and those
    /// asked for beside it (title, departments, office, business address, IM), the many-valued ones as attributed values
    /// under the JSON's own names ("DepartmentsArray"). <see cref="FindPeople"/> leaves out what was not asked for. No
    /// telephone: Exchange Online refuses every telephone field over an address list.
    /// </summary>
    public static JsonObject Person(string name, string email, string? given = null, string? surname = null, string? title = null, string? department = null, string? company = null,
        string? office = null, string? city = null, string? street = null)
    {
        static JsonArray Attributed(JsonNode value) => new(new JsonObject { ["Value"] = value, ["Attributions"] = new JsonArray("0") });
        var mailbox = new JsonObject { ["Name"] = name, ["EmailAddress"] = email, ["RoutingType"] = "SMTP", ["MailboxType"] = "Mailbox" };
        var person = new JsonObject
        {
            ["PersonaId"] = new JsonObject { ["Id"] = "AAUQ" + Convert.ToBase64String(Encoding.UTF8.GetBytes(email)) },
            ["PersonaTypeString"] = "Person",
            ["CreationTimeString"] = "0001-01-02T00:00:00Z",
            ["DisplayName"] = name,
            ["DisplayNameFirstLast"] = name,
            ["DisplayNameLastFirst"] = name,
            ["FileAs"] = "",
            ["GivenName"] = given,
            ["Surname"] = surname,
            ["CompanyName"] = company,
            ["Title"] = title,
            ["EmailAddress"] = mailbox,
            ["EmailAddresses"] = new JsonArray(mailbox.DeepClone(), new JsonObject { ["Name"] = name, ["EmailAddress"] = "/o=ExchangeLabs/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=" + name.Replace(" ", "", StringComparison.Ordinal), ["RoutingType"] = "EX" }),
            ["ImAddress"] = "sip:" + email.ToLowerInvariant(),
            ["WorkCity"] = city,
            ["RelevanceScore"] = 2147483647,
        };
        if (department is not null)
        {
            person["DepartmentsArray"] = Attributed(department);
        }

        if (office is not null)
        {
            person["OfficeLocationsArray"] = Attributed(office);
        }

        if (street is not null)
        {
            person["BusinessAddressesArray"] = Attributed(new JsonObject { ["Street"] = street, ["City"] = city, ["State"] = null, ["Country"] = null, ["PostalCode"] = null, ["PostOfficeBox"] = null, ["Type"] = "Business", ["LocationUri"] = email, ["LocationSource"] = "Contact" });
        }

        return person;
    }

    private HttpResponseMessage GetPeopleFilters(string app)
    {
        if (app != "People")
        {
            return Text(500, "GetPeopleFilters is People's");
        }

        var contacts = _folders.Values.FirstOrDefault(f => f.Distinguished == "contacts");
        var root = _folders.Values.FirstOrDefault(f => f.IsRoot);
        var filters = new JsonArray
        {
            new JsonObject { ["__type"] = "PeopleFilter:#Exchange", ["DisplayName"] = "My Contacts", ["FolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = "AAMkAGMyContactsSearchFolder" }, ["ParentFolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = root?.Id ?? "AAMkRoot" }, ["TotalCount"] = 0, ["SortGroupPriority"] = 1, ["IsReadOnly"] = true },
            new JsonObject { ["__type"] = "PeopleFilter:#Exchange", ["DisplayName"] = "Contacts", ["FolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = contacts?.Id ?? "AAMkContacts" }, ["ParentFolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = root?.Id ?? "AAMkRoot" }, ["TotalCount"] = 0, ["SortGroupPriority"] = 2, ["IsReadOnly"] = false },
        };
        var priority = 1000;
        foreach (var list in _addressLists)
        {
            // the first list's id typed as the request types it, the rest bare: both shapes are read
            var id = priority == 1000 ? new JsonObject { ["__type"] = "AddressListId:#Exchange", ["Id"] = list.Id } : new JsonObject { ["Id"] = list.Id };
            filters.Add(new JsonObject { ["__type"] = "PeopleFilter:#Exchange", ["DisplayName"] = list.Name, ["FolderId"] = id, ["ParentFolderId"] = null, ["TotalCount"] = 0, ["SortGroupPriority"] = priority++, ["IsReadOnly"] = true });
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(filters.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private HttpResponseMessage FindPeople(string app, JsonObject body)
    {
        if (app != "People")
        {
            return Text(500, "FindPeople is People's");
        }

        var id = body["ParentFolderId"]?["BaseFolderId"]?["Id"]?.GetValue<string>();
        var list = _addressLists.FirstOrDefault(l => l.Id == id);
        var asked = body["PersonaShape"]?["AdditionalProperties"]?.AsArray().Select(p => p?["FieldURI"]?.GetValue<string>() ?? "").ToList() ?? [];
        JsonObject answer;
        if (list is null)
        {
            answer = new JsonObject { ["__type"] = "FindPeopleResponseMessage:#Exchange", ["ResponseClass"] = "Error", ["ResponseCode"] = "ErrorInvalidIdMalformed", ["MessageText"] = "Id is malformed." };
        }
        else if (asked.Count > 0 && (RefuseFullerPersonaShape || asked.Any(f => !AskedFields.ContainsKey(f) || RefusedPersonaFields.Contains(f))))
        {
            answer = new JsonObject { ["__type"] = "FindPeopleResponseMessage:#Exchange", ["ResponseClass"] = "Error", ["ResponseCode"] = "ErrorInvalidArgument", ["MessageText"] = "Invalid shape specification." };
        }
        else
        {
            var view = body["IndexedPageItemView"];
            var offset = view?["Offset"]?.GetValue<int>() ?? 0;
            var most = Math.Min(view?["MaxEntriesReturned"]?.GetValue<int>() ?? 100, MostPeoplePerPage);
            var cold = ColdPeopleAnswers > 0;
            if (cold)
            {
                ColdPeopleAnswers--;
            }

            var people = cold ? [] : list.People.Skip(offset).Take(most).Select(p => Shown(p, asked)).ToArray();
            answer = new JsonObject
            {
                ["__type"] = "FindPeopleResponseMessage:#Exchange",
                ["ResultSet"] = new JsonArray(people),
                ["TotalNumberOfPeopleInView"] = cold ? 0 : list.People.Count,
                ["FirstMatchingRowIndex"] = 0,
                ["FirstLoadedRowIndex"] = offset,
                ["ResponseCode"] = "NoError",
                ["ResponseClass"] = "Success",
            };
        }

        var envelope = new JsonObject
        {
            ["Header"] = new JsonObject { ["ServerVersionInfo"] = new JsonObject { ["MajorVersion"] = 15, ["MinorVersion"] = 21, ["Version"] = "V2018_01_08" } },
            ["Body"] = answer,
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>Whether FindPeople refuses any field asked for beside its default shape, as a server stricter than the lab's might.</summary>
    public bool RefuseFullerPersonaShape { get; set; }

    /// <summary>The fields FindPeople refuses beside its default shape, alone or with others, as a server that takes fewer than the lab's.</summary>
    public HashSet<string> RefusedPersonaFields { get; } = new(StringComparer.Ordinal);

    /// <summary>The fields a FindPeople may ask for beside the default shape (those Exchange Online took), and the persona's property each is; any other is refused.</summary>
    private static readonly Dictionary<string, string> AskedFields = new(StringComparer.Ordinal)
    {
        ["persona:Title"] = "Title",
        ["persona:Departments"] = "DepartmentsArray",
        ["persona:OfficeLocations"] = "OfficeLocationsArray",
        ["persona:BusinessAddresses"] = "BusinessAddressesArray",
        ["persona:ImAddress"] = "ImAddress",
    };

    /// <summary>A person as an answer gives it: the default shape's fields and those asked for.</summary>
    private static JsonNode Shown(JsonObject person, List<string> asked)
    {
        var shown = person.DeepClone().AsObject();
        foreach (var (field, property) in AskedFields)
        {
            if (!asked.Contains(field))
            {
                shown.Remove(property);
            }
        }

        return shown;
    }

    /// <summary>An address list of the fake's directory: its people, as FindPeople gives them.</summary>
    internal sealed class AddressListFake(string id, string name)
    {
        public string Id => id;

        public string Name => name;

        public List<JsonObject> People { get; } = [];
    }
}
