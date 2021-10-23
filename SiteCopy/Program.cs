using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MwParserFromScratch;
using MwParserFromScratch.Nodes;
using Newtonsoft.Json;
using WikiClientLibrary;
using WikiClientLibrary.Client;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Sites;
using WikiLink = WikiClientLibrary.WikiLink;

var config = JsonConvert.DeserializeObject<ConfigRoot>(File.ReadAllText("_work/Task.json"));
using var client = new WikiClient { ClientUserAgent = "FurballCenter.SiteCopy/0.1" };
var family = new WikiFamily(client);
family.Register("furry2", "https://furry.huijiwiki.com/api.php");
family.Register("commons", "https://commons.wikimedia.org/w/api.php");
family.Register("zhwp", "https://zh.wikipedia.org/w/api.php");

var homeSite = await family.GetSiteAsync(config.HomeWiki);
await homeSite.LoginAsync(config.Auth.UserName, config.Auth.Password);
Console.WriteLine("Login to {0} as {1}.", homeSite, homeSite.AccountInfo);

var taskPage = new WikiPage(homeSite, "Project:SiteCopy");
await taskPage.RefreshAsync(PageQueryOptions.FetchContent);
var parser = new WikitextParser();
var taskPageRoot = parser.Parse(taskPage.Content);
var importItems = taskPageRoot
    .EnumDescendants()
    .OfType<Template>()
    .Select(t => (Node: t, Item: SiteCopyImportItem.TryParse(t)))
    .Where(t => t.Item is not null)
    .ToList();

Console.WriteLine("Items to import");
foreach (var (_, ii) in importItems) Console.WriteLine(ii);

var newPageCounter = 0;
var updatedPageCounter = 0;
foreach (var (node, ii) in importItems)
{
    var srcLink = await WikiLink.ParseAsync(family, ii!.Source);
    var destTitle = srcLink.FullTitle;
    var destPage = new WikiPage(homeSite, destTitle);
    Console.Write("[{0}]{1} --> [{2}]{3}: ", srcLink.TargetSite, srcLink.FullTitle, homeSite, destTitle);
    await destPage.RefreshAsync();
    if (destPage.Exists) Console.Write("Dest exists. ");

    var srcPage = new WikiPage(srcLink.TargetSite!, srcLink.FullTitle);
    await srcPage.RefreshAsync(PageQueryOptions.FetchContent);
    if (!srcPage.Exists)
    {
        Console.WriteLine("Source does not exist.");
        continue;
    }
    if (srcPage.LastRevision!.Sha1 == destPage.LastRevision!.Sha1)
    {
        Console.WriteLine("Content not changed.");
        continue;
    }

    destPage.Content = srcPage.Content;
    await destPage.UpdateContentAsync(
        $"SiteCopy: Copy from [[{InterwikiPrefixFromWikiName(srcLink.InterwikiPrefix)}:{srcLink.FullTitle}]]; RevId={srcPage.LastRevisionId}.",
        minor: false,
        bot: true);
    node.Arguments.SetValue("revision", new Wikitext(srcPage.LastRevisionId.ToString()));
    node.Arguments.SetValue("revision_time", new Wikitext(srcPage.LastRevision!.TimeStamp.ToString("O")));
    Console.WriteLine("Done.");
    if (srcPage.Exists)
        updatedPageCounter++;
    else
        newPageCounter++;
}

if (newPageCounter + updatedPageCounter > 0)
{
    taskPage.Content = taskPageRoot.ToString();
    await taskPage.UpdateContentAsync($"SiteCopy/Import: Created {newPageCounter} pages; Updated {updatedPageCounter} pages.");
}

string InterwikiPrefixFromWikiName(string wikiPrefix) => wikiPrefix.ToLowerInvariant() switch
{
    "enwp" => "wikipedia:en",
    "zhwp" => "wikipedia:zh",
    _ => wikiPrefix
};

class ConfigRoot
{
    public string HomeWiki { get; set; }

    public AuthInfo Auth { get; set; }

    public List<string> CopyPages { get; set; }
}

record AuthInfo(string UserName, string Password);

record SiteCopyImportItem(string Source, long? Revision, DateTime? RevisionTime)
{
    public static SiteCopyImportItem? TryParse(Template template)
    {
        if (MwParserUtility.NormalizeTitle(template.Name) != "SiteCopy/Import") return null;
        var source = template.Arguments[1].Value.ToString();
        var revision = template.Arguments["revision"]?.Value.ToString();
        var revisionTime = template.Arguments["revision_time"]?.Value.ToString();
        return new SiteCopyImportItem(source,
            revision == null ? null : long.Parse(revision),
            revisionTime == null ? null : DateTime.Parse(revisionTime));
    }
}
