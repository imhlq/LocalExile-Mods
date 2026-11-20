# Global Chat

GlobalChat connects selected Global and Trade channels to shared LocalExile servers, Discord, or simulated chat. Ordinary
area chat stays within the area.

## Defaults

| Channel | Route |
| --- | --- |
| Global 1 | Shared server at `localexile-chat.duckdns.org:6113` |
| Global 2 | Discord, active once the route has a channel ID and the Gateway has a bot token |
| Global 8 | Simulated chat |
| Trade 8 | Simulated trade feed |
| Undefined Global and Trade channels | Local chat |

## Configure channels

In a Waypoint build that supports mod-folder settings, open **Mods → GlobalChat → Settings**. Use **Add entry** or
**Remove** under Global and Trade channel routes. Choose a route type to show its settings.

For manual setup, save `globalchat.json` beside `mod.json` in `Mods/Binaries/globalchat/`. When that file is absent, the
mod reads `data/mods/globalchat.json`. The Gateway reloads the mod after either file changes.

| Route | JSON type | Settings |
| --- | --- | --- |
| Local chat | `blank` | Uses the Gateway’s local chat server |
| Shared server | `server` | `address` and `port` |
| Discord | `discord` | A quoted `discordChannelId` and optional `sendToDiscord` |
| Simulated chat | `fake` | Starts when a local player listens in the room |

```json
{
  "enableTraderBots": true,
  "discordBotToken": "",
  "fallbackRoute": { "type": "blank" },
  "globalChannels": {
    "1": { "type": "server", "address": "localexile-chat.duckdns.org", "port": 6113 },
    "2": { "type": "discord", "discordChannelId": "111111111111111111", "sendToDiscord": false },
    "8": { "type": "fake" }
  },
  "tradeChannels": {
    "8": { "type": "fake" }
  }
}
```

Use channel numbers 1–65534. An explicit `globalChannels` or `tradeChannels` map replaces its defaults. Removing a row
makes that channel use the fallback. An explicit `blank` row keeps that channel local regardless of the fallback.

### Fallback for undefined channels

`fallbackRoute` accepts all four route types. **Local chat** is the default. A shared fallback uses one endpoint for
undefined rooms, with their channel numbers and languages kept separate. A simulated fallback simulates each room while
it has a local listener. A Discord fallback connects all undefined Global and Trade rooms to one Discord channel.
It is receive-only by default. Setting `sendToDiscord` to `true` forwards player messages from those rooms to that same
Discord channel. Give the fallback its own Discord channel ID, distinct from any explicit route.

### Shared servers and whispers

Each shared route can use a separate endpoint. Players on the same endpoint, channel number, channel kind, and chat
language share a room. Whispers use the endpoint of the first explicitly configured shared Global channel by number,
then the first shared Trade channel. With no explicit shared route, whispers stay local.

If a shared server becomes unavailable, its rooms use local chat. The Gateway retries after a one-minute cooldown on
a later area change or channel join. Chat lines and character names travel unencrypted to the shared server. You can
run the chat server for your own group from `src/GameServer.Chat` in LocalExile.

### Discord setup

**Set up your own Discord bot.** Create a Discord app, enable **Message Content Intent** on its **Bot** page, and install the
bot in your server using a Server Install link with the `bot` scope. Copy the token from the **Bot** page and paste it
into **Discord bot token** in Waypoint's GlobalChat settings, or set `discordBotToken` in your local `globalchat.json`.
The input is masked in Waypoint. The local JSON file and its configuration backups store the value as plain text,
so keep them private and out of Git. The shipped default is empty.
See [Discord's bot setup guide](https://docs.discord.com/developers/quick-start/getting-started).

Give the bot **View Channel** and **Read Message History** on the mapped channels. For two-way chat,
also grant **Send Messages**, or **Send Messages in Threads** for a thread, and set that route's `sendToDiscord` to `true`.
Set `discordChannelId` on the Gateway route to the channel or thread ID.

Set `LOCALEXILE_DISCORD_BOT_TOKEN` in the Gateway's launch environment to override the saved value or keep the token
out of the mod config. On Windows, open PowerShell in the LocalExile checkout and run this for a session-only token.

```powershell
$secret = Read-Host 'Discord bot token' -AsSecureString
$previousToken = $env:LOCALEXILE_DISCORD_BOT_TOKEN
try {
    $env:LOCALEXILE_DISCORD_BOT_TOKEN = ([PSCredential]::new('DiscordBot', $secret)).GetNetworkCredential().Password
    .\scripts\dev\live.ps1
}
finally {
    $env:LOCALEXILE_DISCORD_BOT_TOKEN = $previousToken
    $secret.Dispose()
}
```

If you use Waypoint, launch a fresh Waypoint process from that PowerShell session in place of `live.ps1` and
restart the Gateway from Waypoint. For a service-managed Gateway, set the same variable in the service's private
environment and restart the service.

Each explicit Discord route uses a distinct channel or thread ID. Discord messages appear for local players listening
to the mapped room in any game chat language. The bridge ignores bot and webhook messages. Game-to-Discord forwards
accepted player messages only, so the chat server must include channel metadata in its accepted lines.

## Simulated chat

Simulated chat starts automatically when a player listens in a room configured with the `fake` route. The mod unloads
its embedded chat library once all simulated rooms are empty. To disable simulation for a channel, change that
channel's route in the mod settings.

Ambient Global chat matches the category mix of a Global chat capture, with about four in five lines sent as standalone
posts. Short conversations retain the template's speakers and timing and run for two or three lines. Replies to player
messages are triggered separately.

Trader bots offer randomly rolled equipment on simulated channels. Each player sees their own offers. Whisper the
trader `buy` or `invite` while the listing is active to get a trade invitation. Asking `price?` checks the offer.
Automatic equipment offers and named quotes share [trade-listing-examples.json](trade-listing-examples.json). Each
entry selects a `BaseName` for rolled gear or a `UniqueName` from the server's unique catalog, with a `Currency` and
`Amount`. Set `AutoAdvertise` to `false` for request-only stock. `BuybackAmount` enables a fixed buyback in the same
currency. `MinimumLevel` sets the buyer's minimum character level, and `Aliases` add names players can use when
requesting or selling that item.
The invitation arrives about 0.8 seconds after agreement. Accept it within 25 seconds, then place your payment
in the trade window and confirm within 45 seconds. The item goes to your main inventory. Trader bots are unavailable in Solo Self-Found.

Set `"enableTraderBots": false` in `globalchat.json` or turn off **Trader bots** in Waypoint to disable them server-wide.
In game, use `/tradebots off` to stop ads and quotes for your character and cancel any open bot trade.
Use `/tradebots on` to opt back in when the server setting allows it, or `/tradebots status` to check.
Your in-game choice follows the character across areas until GlobalChat reloads or the server restarts.

Ask for **Tabula Rasa** in a Global channel to get a private quote for **5 Chaos Orbs** after 3–6 seconds.
`Tabular Rasa` also works. Whisper `okay` for an invite, then offer 5 Chaos Orbs in one stack or across several stacks.

Use `WTB <item name>` to request listed uniques or eligible equipment by base name. Use `WTS <item name>`
or `WTS` followed by one item link to sell eligible equipment from your main inventory. Traders quote
their own prices, including 4 Chaos Orbs for a Tabula Rasa buyback. Follow the whisper and confirm the
trade to sell the exact item quoted. The trader sends a private thank-you after the exchange succeeds.
Trades use currency items from your main inventory. Most gear is priced in Chaos Orbs. Better rolls can be quoted in
Exalted or Divine Orbs, and exceptional endgame quotes can ask for a Mirror of Kalandra. If payment or space is missing,
fix it and whisper the trader again while the quote is active. If the item being sold has changed or left your
main inventory, post a new `WTS` with the item linked to get a fresh quote. You can place items in the offer and take
them back. Hover over the bot's items to unlock the client's Accept button. The bot confirms only the exact quoted
item or currency total, with no extras. Cancelling returns your offer to your inventory.

On simulated Trade routes, the feed uses variable gaps calibrated to about two or three posts per active minute.
Service ads make up most of the feed and include boss carries, voidstones, and five-way runs. Advertisers can repost
the same line under the same character name. Service ads are chat-only. Equipment ads remain tradable, and requests
accept shorthand such as `WTB TABULA 15c`, `WTS 30c <item>`, and `<item> WTS`. Traders send their own quotes.

## Compatibility

- **Required** — Merge the LocalExile server PR from `pr/globalchat-hooks` before the GlobalChat expansion PR.
- The local trader-bot branch also requires the server's virtual trade isolation support from `work/trader-bots`.
- **Optional** — Waypoint's `pr/mod-channel-settings` PR adds the channel controls in **Mods → GlobalChat → Settings**.
  You can also edit `globalchat.json` directly.

GlobalChat 1.1.4 supports LocalExile v0.4.3-beta through v0.5.2-beta.
