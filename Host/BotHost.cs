using System.Collections.Concurrent;
using IRCBot.Shared;

namespace IRCBot.Host;

// Owns the collection of bots and applies control commands to them.
public sealed class BotHost
{
    private readonly ConcurrentDictionary<string, IrcBot> _bots = new();

    public IReadOnlyCollection<BotInfo> List() => _bots.Values.Select(b => b.ToInfo()).ToList();

    public BotInfo Add(string nick, string host, int port, IEnumerable<string> channels)
    {
        var bot = new IrcBot(nick, host, port, channels);
        _bots[bot.Id] = bot;
        return bot.ToInfo();
    }

    public bool Remove(string id)
    {
        if (_bots.TryRemove(id, out var bot)) { bot.Stop(); return true; }
        return false;
    }

    public bool Start(string id) => With(id, b => b.Start());
    public bool Stop(string id) => With(id, b => b.Stop());
    public bool Join(string id, string channel) => With(id, b => b.Join(channel));
    public bool Part(string id, string channel) => With(id, b => b.Part(channel));
    public bool Say(string id, string target, string text) => With(id, b => b.Say(target, text));

    public void StopAll() { foreach (var b in _bots.Values) b.Stop(); }

    private bool With(string id, Action<IrcBot> action)
    {
        if (_bots.TryGetValue(id, out var bot)) { action(bot); return true; }
        return false;
    }
}
