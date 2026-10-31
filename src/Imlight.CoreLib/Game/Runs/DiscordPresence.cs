/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

/*
 * Discord Rich Presence for a run: "Playing Spiral Gauntlet", the wizard, their level, tier and what they are doing,
 * on the player's Discord profile. The server runs on the player's own PC, next to their Discord app, and talks to it
 * over Discord's local IPC pipe (\\.\pipe\discord-ipc-0 to 9): a handshake with the application id, then SET_ACTIVITY.
 * Nothing goes over the internet from here, and nothing happens when Discord is not running.
 *
 * [Discord] RichPresence = false turns it off; ApplicationId is the Spiral Gauntlet application on Discord's
 * developer portal (its art assets: logo, arc1, arc2, arc3). The status clears when no wizard has updated it for
 * StaleSeconds (they logged out), and Discord clears it itself when the server stops.
 */

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Runs;

internal static class DiscordPresence {

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Discord.RichPresence", true);

    private static readonly string s_applicationId
        = ConfigurationManager.GetValue("Discord.ApplicationId", "1566185912138989618")?.Trim() ?? "";

    private static readonly TimeSpan s_stale
        = TimeSpan.FromSeconds(Math.Max(30, ConfigurationManager.GetValue("Discord.StaleSeconds", 120)));

    private const int OP_HANDSHAKE = 0;
    private const int OP_FRAME = 1;

    private static readonly object s_lock = new();
    private static Timer s_timer;
    private static NamedPipeClientStream s_pipe;
    private static DateTime s_nextConnectTry = DateTime.MinValue;

    // What Discord should show (null: nothing), whether it was sent, and when a wizard last asked for it.
    private static JsonObject s_activity;
    private static bool s_sent = true;
    private static DateTime s_lastTouch = DateTime.MinValue;

    /// <summary>One status for Discord. Large and small images are the application's art asset names.</summary>
    internal sealed record Activity(string Details, string State, long? StartUnix, long? EndUnix,
                                    string LargeImage, string LargeText, string SmallImage, string SmallText);

    /// <summary>Shows this status (sent within a few seconds, or as soon as Discord is open).</summary>
    internal static void Set(Activity activity) {
        if (!Enabled || !OperatingSystem.IsWindows() || string.IsNullOrEmpty(s_applicationId) || activity is null) {
            return;
        }

        var json = new JsonObject();
        var assets = new JsonObject();
        Put(json, "details", activity.Details);
        Put(json, "state", activity.State);
        Put(assets, "large_image", activity.LargeImage);
        Put(assets, "large_text", activity.LargeText);
        Put(assets, "small_image", activity.SmallImage);
        Put(assets, "small_text", activity.SmallText);
        if (assets.Count > 0) {
            json["assets"] = assets;
        }

        if (activity.EndUnix is { } end) {
            json["timestamps"] = new JsonObject { ["end"] = end };
        }
        else if (activity.StartUnix is { } start) {
            json["timestamps"] = new JsonObject { ["start"] = start };
        }

        lock (s_lock) {
            s_lastTouch = DateTime.UtcNow;
            if (s_activity is null || !JsonNode.DeepEquals(s_activity, json)) {
                s_activity = json;
                s_sent = false;
            }

            s_timer ??= new Timer(_ => Pump(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        ThreadPool.QueueUserWorkItem(_ => Pump());
    }

    // Discord's text fields take 2 to 128 characters; a shorter one is left out.
    private static void Put(JsonObject json, string key, string text) {
        text = (text ?? "").Trim();
        if (text.Length > 128) {
            text = text[..127] + "…";
        }

        if (text.Length >= 2) {
            json[key] = text;
        }
    }

    private static void Pump() {
        lock (s_lock) {
            try {
                // Logged out (no wizard has asked for a while): clear it.
                if (s_activity is not null && DateTime.UtcNow - s_lastTouch > s_stale) {
                    s_activity = null;
                    s_sent = false;
                }

                if (s_sent || !Connected()) {
                    return;
                }

                Send(OP_FRAME, new JsonObject {
                    ["cmd"] = "SET_ACTIVITY",
                    ["args"] = new JsonObject {
                        ["pid"] = Environment.ProcessId,
                        ["activity"] = s_activity?.DeepClone(),
                    },
                    ["nonce"] = Guid.NewGuid().ToString(),
                });
                Read();
                s_sent = true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException
                                          or UnauthorizedAccessException or JsonException or TimeoutException) {
                // Discord closed, or restarted: try again in a little while.
                Logger.Debug("[Discord] Rich Presence lost its connection: {0}", Logger.Args(e.Message));
                Disconnect();
                s_sent = false;
            }
        }
    }

    private static bool Connected() {
        if (s_pipe is { IsConnected: true }) {
            return true;
        }

        if (DateTime.UtcNow < s_nextConnectTry) {
            return false;
        }

        s_nextConnectTry = DateTime.UtcNow.AddSeconds(15);
        Disconnect();
        for (var i = 0; i < 10; i++) {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.None);
            try {
                pipe.Connect(150);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) {
                pipe.Dispose();

                continue;
            }

            s_pipe = pipe;
            Send(OP_HANDSHAKE, new JsonObject { ["v"] = 1, ["client_id"] = s_applicationId });
            var (op, reply) = Read();
            if (op != OP_FRAME || !reply.Contains("\"READY\"", StringComparison.Ordinal)) {
                Logger.Warning("[Discord] Rich Presence handshake refused: {0}", Logger.Args(reply));
                Disconnect();

                return false;
            }

            Logger.Information("[Discord] Rich Presence connected to Discord (discord-ipc-{0}).", Logger.Args(i));
            // A new connection starts with nothing shown: send the current status again.
            s_sent = false;

            return true;
        }

        return false;
    }

    private static void Send(int op, JsonNode payload) {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        s_pipe.Write(frame, 0, frame.Length);
        s_pipe.Flush();
    }

    private static (int Op, string Body) Read() {
        var header = new byte[8];
        ReadExactly(header);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > 1 << 20) {
            throw new IOException($"Discord sent a frame of {length} bytes.");
        }

        var body = new byte[length];
        ReadExactly(body);

        return (op, Encoding.UTF8.GetString(body));
    }

    private static void ReadExactly(byte[] buffer) {
        var read = 0;
        while (read < buffer.Length) {
            var got = s_pipe.Read(buffer, read, buffer.Length - read);
            if (got <= 0) {
                throw new IOException("Discord closed the pipe.");
            }

            read += got;
        }
    }

    private static void Disconnect() {
        try {
            s_pipe?.Dispose();
        }
        catch (Exception) {
            // Already gone.
        }

        s_pipe = null;
    }

}
