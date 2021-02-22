using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Logger;
using static Nuke.Common.Tools.Git.GitTasks;

[GitHubActions(
    "daily",
    GitHubActionsImage.Ubuntu1804,
    InvokedTargets = new[] {nameof(SaveTweets)})]
[CheckBuildProjectConfigurations]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Tweet);

    [Parameter] string ConsumerKey;
    [Parameter] string ConsumerSecret;
    [Parameter] string AccessToken;
    [Parameter] string AccessTokenSecret;

    AbsolutePath TweetDirectory => RootDirectory / "tweets";
    AbsolutePath SentTweetsFile => RootDirectory / "tweets.csv";
    List<SentTweet> SentTweets = new List<SentTweet>();

    Target LoadTweets => _ => _
        .Executes(() =>
        {
            using var reader = new StreamReader(SentTweetsFile);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<SentTweetMap>();
            SentTweets = csv.GetRecords<SentTweet>().ToList();
        });

    Target UpdateTweets => _ => _
        .DependsOn(LoadTweets)
        .Requires(() => ConsumerKey)
        .Requires(() => ConsumerSecret)
        .Requires(() => AccessToken)
        .Requires(() => AccessTokenSecret)
        .Executes(() =>
        {
            var client = new TwitterClient(
                new TwitterCredentials(
                    ConsumerKey,
                    ConsumerSecret,
                    AccessToken,
                    AccessTokenSecret));

            SentTweets.Where(x => !x.FavoriteCount.HasValue)
                .ForEach(x =>
                {
                    var tweet = client.Tweets.GetTweetAsync(x.Id).GetAwaiter().GetResult();
                    x.FavoriteCount = tweet.FavoriteCount;
                    x.RetweetCount = tweet.RetweetCount;
                    x.ReplyCount = tweet.ReplyCount;
                });
        });

    [Parameter] readonly string TweetBaseName;

    Target Tweet => _ => _
        .DependsOn(LoadTweets)
        .After(UpdateTweets)
        .Requires(() => ConsumerKey)
        .Requires(() => ConsumerSecret)
        .Requires(() => AccessToken)
        .Requires(() => AccessTokenSecret)
        .Executes(async () =>
        {
            string GetTweetBaseName()
            {
                var tweetsOrderedByLastPublish = TweetDirectory
                    .GlobFiles("*.md")
                    .Select(x => Path.GetFileNameWithoutExtension(x))
                    .OrderBy(x => SentTweets.FindIndex(y => y.Name == x))
                    .ThenBy(x => x).ToList();
                return tweetsOrderedByLastPublish.First().ReplaceRegex("\\d+.*", x => string.Empty);
            }

            var client = new TwitterClient(
                new TwitterCredentials(
                    ConsumerKey,
                    ConsumerSecret,
                    AccessToken,
                    AccessTokenSecret));

            var tweetBaseName = TweetBaseName ?? GetTweetBaseName();
            var tweetFiles = TweetDirectory
                .GlobFiles($"{tweetBaseName}*.md")
                .Select(x => x.ToString())
                .OrderBy(x => x).ToList();

            foreach (var tweetFile in tweetFiles)
            {
                var tweetName = Path.GetFileNameWithoutExtension(tweetFile);
                var text = ReadAllText(tweetFile);
                var media = TweetDirectory
                    .GlobFiles($"{tweetName}*.png", $"{tweetName}*.jpeg", $"{tweetName}*.jpg", $"{tweetName}*.gif")
                    .Select(async x => await client.Upload.UploadTweetImageAsync(
                        new UploadTweetImageParameters(ReadAllBytes(x))
                        {
                            MediaCategory = x.ToString().EndsWithOrdinalIgnoreCase("gif")
                                ? MediaCategory.Gif
                                : MediaCategory.Image
                        }))
                    .Select(x => x.Result).ToList();

                var tweetParameters = new PublishTweetParameters
                {
                    InReplyToTweetId = SentTweets.FirstOrDefault()?.Id,
                    Text = text,
                    Medias = media
                };

                var tweet = await client.Tweets.PublishTweetAsync(tweetParameters);
                Info($"Sent tweet: {tweetName} [{tweet.Url}]");
                SentTweets.Add(new SentTweet
                {
                    Id = tweet.Id,
                    DateTime = DateTime.Now,
                    Name = tweetName,
                    Url = tweet.Url
                });
            }
        });

    [CI] readonly GitHubActions GitHubActions;
    [Parameter] readonly string GitHubToken;
    [GitRepository] readonly GitRepository Repository;

    Target SaveTweets => _ => _
        .DependsOn(LoadTweets)
        .Triggers(Commit)
        // .TriggeredBy(Tweet)
        .Executes(() =>
        {
            SentTweets.Add(new SentTweet(){Name="foo", Id = 123, Url = "moooo"});

            using var writer = new StreamWriter(SentTweetsFile);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<SentTweetMap>();
            csv.WriteRecords(SentTweets);
        });

    Target Commit => _ => _
        .Executes(() =>
        {
            var remote = $"https://{GitHubActions.GitHubActor}:{GitHubToken}@github.com/{GitHubActions.GitHubRepository}";
            Git($"remote set-url origin {remote.DoubleQuote()}");
            Git($"config user.name {"Matthias Koch".DoubleQuote()}");
            Git($"config user.email {"ithrowexceptions@gmail.com".DoubleQuote()}");
            Git($"add {SentTweetsFile}");
            Git($"commit -m {"Update".DoubleQuote()}");
            Git($"push origin HEAD:{Repository.Branch}");
        });

    class SentTweet
    {
        public string Name;
        public DateTime DateTime;
        public string Url;
        public long Id;
        public int? FavoriteCount;
        public int? RetweetCount;
        public int? ReplyCount;
    }

    [UsedImplicitly]
    class SentTweetMap : ClassMap<SentTweet>
    {
        [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor")]
        public SentTweetMap()
        {
            Map(x => x.Name);
            Map(x => x.DateTime);
            Map(x => x.Url);
            Map(x => x.Id);
            Map(x => x.FavoriteCount);
            Map(x => x.RetweetCount);
            Map(x => x.ReplyCount);
        }
    }
}