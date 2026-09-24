using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DesktopPlanner.Application;
using DesktopPlanner.Domain;

namespace DesktopPlanner.Infrastructure;

public sealed class CalendarConflictException() : Exception("Событие изменилось в iCloud. Повторите синхронизацию — обе версии будут сохранены.");

public sealed class ICloudCalendarProvider : ICalendarProvider, IDisposable
{
    private static readonly XNamespace Dav = "DAV:", Cal = "urn:ietf:params:xml:ns:caldav";
    private readonly HttpClient client;
    private readonly AuthenticationHeaderValue authorization;
    public ICloudCalendarProvider(string userName, string password, HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
        authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password)));
    }
    public static Uri ValidateUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !System.Text.RegularExpressions.Regex.IsMatch(uri.Host, @"^(caldav|p\d+-caldav)\.icloud\.com$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Ожидался защищённый адрес календаря iCloud.");
        return uri;
    }
    private static string Resolve(string root, string href) => ValidateUri(new Uri(ValidateUri(root), href).AbsoluteUri).AbsoluteUri;
    private static string Resource(string calendar, string href)
    {
        var uri = ValidateUri(Resolve(calendar, href)); var parent = ValidateUri(calendar);
        if (uri.Authority != parent.Authority || !uri.AbsolutePath.StartsWith(parent.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)
            || uri.AbsolutePath == parent.AbsolutePath || uri.Query.Length > 0)
            throw new InvalidOperationException("Ресурс находится вне выбранного календаря.");
        return uri.AbsoluteUri;
    }
    private async Task<HttpResponseMessage> SendAsync(string method, string url, string? body, string? depth, string? etag, bool creating, CancellationToken ct)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), ValidateUri(url));
            request.Headers.Authorization = authorization;
            if (depth is not null) request.Headers.Add("Depth", depth);
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            else if (creating) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, method == "PUT" ? "text/calendar" : "application/xml");
            var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode is 301 or 302 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidOperationException("Пустое перенаправление iCloud.");
                url = Resolve(url, location.ToString()); continue;
            }
            return response;
        }
        throw new InvalidOperationException("Слишком много перенаправлений iCloud.");
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new CalendarConflictException();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("iCloud отклонил вход. Проверьте Apple Account и пароль приложения.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("iCloud запретил доступ. Проверьте права календаря и пароль приложения.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"iCloud: ошибка HTTP {(int)response.StatusCode}. Изменения сохранены на компьютере.");
    }
    private async Task<(XDocument Document, string Url)> XmlAsync(string method, string url, XElement body, string depth, CancellationToken ct)
    {
        using var response = await SendAsync(method, url, body.ToString(), depth, null, false, ct);
        EnsureSuccess(response);
        if ((int)response.StatusCode != 207) throw new InvalidOperationException("iCloud вернул неполный ответ календаря.");
        using var reader = XmlReader.Create(new StringReader(await response.Content.ReadAsStringAsync(ct)),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != Dav + "multistatus") throw new InvalidOperationException("Неверный ответ CalDAV.");
        return (document, response.RequestMessage?.RequestUri?.AbsoluteUri ?? url);
    }
    private static IEnumerable<XElement> Properties(XElement response) => response.Elements(Dav + "propstat")
        .Where(p => (string?)p.Element(Dav + "status") is { } status && status.Split(' ').ElementAtOrDefault(1) == "200")
        .Elements(Dav + "prop");
    private async Task<string> FindHrefAsync(string url, XName property, CancellationToken ct)
    {
        var (xml, finalUrl) = await XmlAsync("PROPFIND", url, new XElement(Dav + "propfind", new XElement(Dav + "prop", new XElement(property))), "0", ct);
        var href = xml.Root!.Elements(Dav + "response").SelectMany(Properties).Elements(property).Elements(Dav + "href").FirstOrDefault()?.Value;
        return Resolve(finalUrl, href ?? throw new InvalidOperationException("iCloud не вернул адрес календаря. Включите Календарь в iCloud."));
    }
    public async Task<IReadOnlyList<RemoteCalendar>> DiscoverAsync(CalendarAccount account, CancellationToken cancellationToken)
    {
        var principal = await FindHrefAsync("https://caldav.icloud.com/", Dav + "current-user-principal", cancellationToken);
        var home = await FindHrefAsync(principal, Cal + "calendar-home-set", cancellationToken);
        var (xml, finalUrl) = await XmlAsync("PROPFIND", home, new XElement(Dav + "propfind", new XElement(Dav + "prop",
            new XElement(Dav + "displayname"), new XElement(Dav + "resourcetype"), new XElement(Dav + "current-user-privilege-set"),
            new XElement(Cal + "supported-calendar-component-set"))), "1", cancellationToken);
        var result = new List<RemoteCalendar>();
        foreach (var response in xml.Root!.Elements(Dav + "response"))
        {
            var props = Properties(response).ToList();
            if (!props.Elements(Dav + "resourcetype").Elements(Cal + "calendar").Any()) continue;
            if (!props.Elements(Cal + "supported-calendar-component-set").Elements(Cal + "comp").Any(c => (string?)c.Attribute("name") == "VEVENT")) continue;
            var privileges = props.Elements(Dav + "current-user-privilege-set").Descendants().Select(e => e.Name).ToHashSet();
            if (!privileges.Contains(Dav + "all") && !privileges.Contains(Dav + "write")
                && !(privileges.Contains(Dav + "write-content") && privileges.Contains(Dav + "bind") && privileges.Contains(Dav + "unbind"))) continue;
            var href = response.Element(Dav + "href")?.Value ?? throw new InvalidOperationException("Календарь без адреса.");
            result.Add(new RemoteCalendar(Resolve(finalUrl, href).TrimEnd('/') + "/", props.Elements(Dav + "displayname").FirstOrDefault()?.Value ?? "Календарь"));
        }
        return result;
    }
    public async Task<CalendarChangeSet> GetChangesAsync(string calendarId, IReadOnlyDictionary<string, string?> knownETags, CancellationToken cancellationToken)
    {
        // ETag inventory is also the fallback for servers that invalidate/omit sync-token.
        // Only changed resources are downloaded; deletions are applied after a complete report.
        var (xml, _) = await XmlAsync("REPORT", calendarId, new XElement(Cal + "calendar-query",
            new XElement(Dav + "prop", new XElement(Dav + "getetag")), new XElement(Cal + "filter",
                new XElement(Cal + "comp-filter", new XAttribute("name", "VCALENDAR"), new XElement(Cal + "comp-filter", new XAttribute("name", "VEVENT"))))), "1", cancellationToken);
        var ids = new HashSet<string>(StringComparer.Ordinal); var changed = new List<CalendarEvent>();
        foreach (var response in xml.Root!.Elements(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value ?? throw new InvalidOperationException("Неполный список событий iCloud.");
            // iCloud can include the calendar collection itself in a calendar-query response.
            // It is not an event resource and must not enter the ETag/deletion inventory.
            var resolved = ValidateUri(Resolve(calendarId, href));
            var collection = ValidateUri(calendarId);
            if (resolved.Authority == collection.Authority
                && resolved.AbsolutePath.TrimEnd('/') == collection.AbsolutePath.TrimEnd('/')
                && resolved.Query.Length == 0) continue;
            var id = Resource(calendarId, href);
            var etag = Properties(response).Elements(Dav + "getetag").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(etag)) throw new InvalidOperationException("iCloud не вернул версию события. Синхронизация остановлена.");
            ids.Add(id);
            if (knownETags.TryGetValue(id, out var known) && known == etag) continue;
            changed.Add(await ReadAsync(calendarId, id, cancellationToken));
        }
        return new CalendarChangeSet(changed, ids.ToArray());
    }
    private async Task<CalendarEvent> ReadAsync(string calendarId, string id, CancellationToken ct)
    {
        using var response = await SendAsync("GET", Resource(calendarId, id), null, null, null, false, ct);
        EnsureSuccess(response);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Слишком большой ресурс календаря.");
        var item = CalendarCodec.Read(text);
        item.CalendarId = calendarId; item.ExternalId = id; item.ETag = response.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("iCloud не вернул ETag события.");
        return item;
    }
    public async Task<CalendarEvent> SaveAsync(CalendarEvent item, string? expectedETag, CancellationToken cancellationToken)
    {
        var calendar = item.CalendarId ?? throw new InvalidOperationException("Календарь не выбран.");
        var id = Resource(calendar, item.ExternalId ?? throw new InvalidOperationException("Нет адреса события."));
        using var response = await SendAsync("PUT", id, CalendarCodec.Write(item), null, expectedETag, expectedETag is null, cancellationToken);
        EnsureSuccess(response);
        // Read back server-normalized ICS and its matching ETag, including after a successful create.
        var saved = await ReadAsync(calendar, id, cancellationToken);
        if (response.Headers.ETag is { } tag && tag.ToString() != saved.ETag) throw new CalendarConflictException();
        return saved;
    }
    public async Task DeleteAsync(string calendarId, string externalId, string? expectedETag, CancellationToken cancellationToken)
    {
        if (expectedETag is null) throw new InvalidOperationException("Удаление без известной версии события запрещено.");
        using var response = await SendAsync("DELETE", Resource(calendarId, externalId), null, null, expectedETag, false, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound) EnsureSuccess(response);
    }
    public void Dispose() => client.Dispose();
}
