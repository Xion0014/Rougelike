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

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imcodec.ObjectProperty.TypeCache;
using Microsoft.VisualBasic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed class ZonePathSupervisor(Core.Zone zone) : ZoneEntitySupervisor(zone) {

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // The game client splits path data, node data spawn data into three separate data entities.
        // Nodes exist in 3D space, paths connect nodes, and creatures spawn on paths.
        var pathData = message.PathData;
        var nodeData = message.NodeData;
        var creatureSpawnData = message.SpawnData;

        var nodesById = new Dictionary<ulong, NodeObject>();
        foreach (var node in nodeData.m_nodeList) {
            nodesById.TryAdd(node.m_id.Full, node);
        }

        LogWispSpawners(pathData, creatureSpawnData);

        // Roguelike runs swap a street's creatures among the ones that spawn in the zone: the game only loads the
        // models of those, so a creature from elsewhere would fight invisibly.
        Imlight.CoreLib.Game.Runs.RoguelikeCreatures.RegisterRoster(Zone?.ZonePath,
            creatureSpawnData?.m_spawners?
                .Where(spawner => spawner?.m_spawnList is not null)
                .SelectMany(spawner => spawner.m_spawnList)
                .Where(item => item?.m_objectInfo is not null && !IsWisp(item.m_objectInfo))
                .Select(item => (ulong) item.m_objectInfo.m_templateID));

        foreach (var path in pathData.m_pathList) {
            if (!TryGetNodesForPath(path, nodesById, out var nodes)) {
                continue;
            }

            var creatures = GetCreaturesForPath(path, creatureSpawnData);
            var pathActor = Context.ActorOf(Props.Create(() => new ZonePath(path, nodes, creatures, ZoneRef, Zone)));
            BeginEntityLoad(pathActor, path.m_name);
        }

        ReportLoadedWhenEntitiesLoad();
    }

    private static bool TryGetNodesForPath(PathObjectTemplate path, Dictionary<ulong, NodeObject> nodesById,
                                           out List<NodeObject> nodes) {
        nodes = new List<NodeObject>(path.m_nodeIDs.Count);
        foreach (var id in path.m_nodeIDs) {
            if (!nodesById.TryGetValue(id.Full, out var node)) {
                Logger.Error("Path {Path} names node {NodeId}, which is not in the node data; skipping the path.",
                    Logger.Args(path.m_name, id.Full));

                return false;
            }

            nodes.Add(node);
        }

        return true;
    }

    /// <summary>
    /// Writes one log line for every wisp spawner in this zone's data, so a missing wisp can be traced to its
    /// spawner settings or to a path the server never loads.
    /// </summary>
    private void LogWispSpawners(PathTemplateList pathData, SpawnManager spawnData) {
        if (spawnData?.m_spawners is null) {
            return;
        }

        var paths = new Dictionary<ulong, PathObjectTemplate>();
        foreach (var path in pathData?.m_pathList ?? []) {
            paths.TryAdd(path.m_id.Full, path);
        }

        foreach (var spawner in spawnData.m_spawners) {
            var wisps = spawner.m_spawnList?.Where(x => x.m_objectInfo is not null && IsWisp(x.m_objectInfo)).ToList();
            if (wisps is null || wisps.Count == 0) {
                continue;
            }

            foreach (var wisp in wisps) {
                var info = wisp.m_objectInfo;
                var template = CoreObjectFactory.GetCoreTemplate(info.m_templateID) as GameObjectTemplate;
                var pathName = paths.TryGetValue(info.m_pathID.Full, out var path) ? path.m_name.ToString() : "(no such path)";
                Logger.Information(
                    "Wisp spawner {Spawner} in {Zone}: {Wisp} on path {Path}, active {Active}, max {Max}, at least one {AtLeastOne}, every {Seconds}s, requirements {Reqs}.",
                    Logger.Args(spawner.m_name.ToString(), Zone.ZoneName, template?.m_objectName.ToString() ?? info.m_templateID.ToString(),
                                pathName, spawner.m_active, spawner.m_maxNumberOfSpawns, spawner.m_atLeastOneSpawn,
                                spawner.m_spawnTime, spawner.m_globalDynamicReqs is not null));
            }
        }
    }

    internal static bool IsWisp(SpawnObjectInfo info)
        => CoreObjectFactory.GetCoreTemplate(info.m_templateID) is GameObjectTemplate template
           && template.m_objectName.ToString().Contains("Wisp");

    private static List<SpawnObject> GetCreaturesForPath(PathObjectTemplate path, SpawnManager spawnData) {
        var creatureList = new List<SpawnObject>();
        foreach (var spawnObject in spawnData.m_spawners) {
            var spawnList = spawnObject.m_spawnList;
            if (spawnList is null || spawnList.Count <= 0) {
                continue;
            }

            // Check the spawn requirements for this mob, if they exist.
            // If the requirements are not met, skip this mob.
            if (spawnObject.m_globalDynamicReqs is not null) {
                var requirementsMet = RequirementDispatcher.EvaluateRequirements(
                    requirements: spawnObject.m_globalDynamicReqs,
                    context: new ZoneRequirementContext(
                        requirements: spawnObject.m_globalDynamicReqs,
                        playerRef: null,
                        playerObj: null,
                        wizard: null,
                        zoneRef: null,
                        triggerName: null
                    )
                );

                if (!requirementsMet) {
                    if (spawnObject.m_spawnList.Any(x => x.m_objectInfo?.m_pathID == path.m_id && IsWisp(x.m_objectInfo))) {
                        Logger.Information("Wisp spawner {0} on path {1} is off: its spawn requirements are not met.",
                            Logger.Args(spawnObject.m_id, path.m_name));
                    }

                    continue;
                }
            }

            // If the path ID matches, add it to the list.
            var matchedCreatures = spawnList.Any(x => x.m_objectInfo is not null
                                              && x.m_objectInfo.m_pathID == path.m_id);

            if (matchedCreatures) {
                creatureList.Add(spawnObject);
            }
        }

        return creatureList;
    }

}
