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
 * The game's tutorial tips (the NPC portraits that pop up on the right as you level and quest). The game keeps them
 * itself (Root.wad TutorialTips/*.ttip) and shows one when its event happens, unless the tip is in the player's tip
 * log, which the server sends (format from Niduus's TutorialTipGate):
 *   MSG_UPDATETUTORIALTIPLOG: uint 0x2DEC0DED, uint version 1, ushort count, count uint tip IDs.
 * The game never shows a tip in the log (bar the few "always repeatable" ones).
 *
 * [Roguelike] TutorialTips = false (the default) puts every tip in the log, so a run shows none: they are for the
 * normal game, and their window art (GUI/Art/Art_Tutorial_Log_01.dds) is missing from the game files, which made the
 * game retry it every frame until it lagged out. The game asks for the log at login (MSG_REQUESTTUTORIALTIPLOG); it
 * went unanswered before.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

internal class TutorialTipService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string TIP_FOLDER = "TutorialTips/";
    private const uint LOG_MAGIC = 0x2DEC0DED;
    private const uint LOG_VERSION = 1;

    /// <summary>[Roguelike] TutorialTips: the game's tutorial tips show. Off by default.</summary>
    private static readonly bool s_showTips = ConfigurationManager.GetValue("Roguelike.TutorialTips", false);

    private static readonly Lazy<uint[]> s_allTips = new(LoadTipIds, LazyThreadSafetyMode.ExecutionAndPublication);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TutorialTipService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG))]
    private void ReceiveRequestTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG message) {
        if (s_showTips) {
            return;
        }

        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG {
                GlobalID = wizard.GameObjectID,
                TutorialTipData = new ByteString(Encode(s_allTips.Value)),
            });
        }
        catch (Exception ex) {
            Logger.Error("Tutorial tips | Sending the tip log failed: {0}", Logger.Args(ex));
        }
    }

    // A tip the player read, or the game's own copy of the log: nothing to keep while every tip is in the log.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP))]
    private void ReceiveReadTutorialTip(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP message) { }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG))]
    private void ReceiveUpdateTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG message) { }

    private static byte[] Encode(uint[] log) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(LOG_MAGIC);
        writer.Write(LOG_VERSION);
        writer.Write((ushort) log.Length);
        foreach (var id in log) {
            writer.Write(id);
        }

        writer.Flush();

        return stream.ToArray();
    }

    private static uint[] LoadTipIds() {
        var ids = new SortedSet<uint>();
        try {
            foreach (var (entry, data) in RootArchiveLoader.GetDirectoryStream(TIP_FOLDER)) {
                if (data is not { } bytes) {
                    continue;
                }

                try {
                    if (new BindSerializer().Deserialize(bytes.ToArray(), out TipData tip) && tip is not null
                        && !tip.m_alwaysRepeatable && (ulong) tip.m_id is > 0 and <= uint.MaxValue) {
                        ids.Add((uint) (ulong) tip.m_id);
                    }
                }
                catch (Exception ex) {
                    Logger.Warning("Tutorial tips | could not read {0}: {1}", Logger.Args(entry.FileName, ex.Message));
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Tutorial tips | reading the game's tips failed: {0}", Logger.Args(ex.Message));
        }

        Logger.Information("Tutorial tips | {0} tips in the game files are switched off (TutorialTips = false).", Logger.Args(ids.Count));

        return [.. ids.Take(ushort.MaxValue)];
    }

}
