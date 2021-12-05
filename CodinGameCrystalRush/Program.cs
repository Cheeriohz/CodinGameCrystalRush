using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

/**
 * Deliver more ore to hq (left side of the map) than your opponent. Use radars to find ore but beware of traps!
 **/
class Player
{
    static void Main(string[] args)
    {
        string[] inputs;
        inputs = Console.ReadLine().Split(' ');
        int width = int.Parse(inputs[0]);
        int height = int.Parse(inputs[1]); // size of the map

        Dictionary<(int, int), OreStruct> oreData = new Dictionary<(int, int), OreStruct>();

        Dictionary<int, Bot> bots = new Dictionary<int, Bot>();
        BotOverSeer overSeer = new BotOverSeer(height, width, bots, oreData);

        // game loop
        while(true)
        {
            inputs = Console.ReadLine().Split(' ');

            Stopwatch executionTimer = new Stopwatch();
            executionTimer.Start();
            int myScore = int.Parse(inputs[0]); // Amount of ore delivered
            int opponentScore = int.Parse(inputs[1]);
            for(int i = 0; i < height; i++)
            {
                inputs = Console.ReadLine().Split(' ');
                for(int j = 0; j < width; j++)
                {
                    string ore = inputs[2 * j];// amount of ore or "?" if unknown
                    int hole = int.Parse(inputs[2 * j + 1]);// 1 if cell has a hole

                    if(overSeer.ProcessRawOreStruct((j, i),
                        new OreStruct
                        {
                            OreCount = ore == "?"
                                        ? i == 0
                                            ? 0
                                            : -1
                                        : int.TryParse(ore, out int oreAmount) ? oreAmount : -1,
                            HolePresent = hole == 1,
                            HasBeenIdentified = false
                        }))
                    {
                        //oreData[(j, i)].LogOreStruct(i, j);
                    }
                }
            }
            inputs = Console.ReadLine().Split(' ');
            int entityCount = int.Parse(inputs[0]); // number of entities visible to you
            overSeer.RadarCoolDown = int.Parse(inputs[1]); // turns left until a new radar can be requested
            overSeer.TrapCoolDown = int.Parse(inputs[2]); // turns left until a new trap can be requested

            EntityData[] roundEntities = new EntityData[entityCount];
            for(int i = 0; i < entityCount; i++)
            {
                inputs = Console.ReadLine().Split(' ');
                EntityData entityData = new EntityData
                {
                    EntityId = int.Parse(inputs[0]),
                    EntityType = (EntityType)int.Parse(inputs[1]),
                    X = int.Parse(inputs[2]),
                    Y = int.Parse(inputs[3]),
                    ItemType = (ItemType)int.Parse(inputs[4])
                };

                switch(entityData.EntityType)
                {
                    case EntityType.BOT_FRIEND:
                        if(!bots.ContainsKey(entityData.EntityId))
                        {
                            bots[entityData.EntityId] = new Bot(entityData, overSeer);
                        }
                        else
                        {
                            bots[entityData.EntityId].ReconcileEntityData(entityData);
                        }
                        break;
                    case EntityType.BOT_ENEMY:
                        if(overSeer.ProcessRawEnemyData(entityData))
                        {
                            //entityData.LogEntity();
                        }
                        break;
                    case EntityType.TRAP:
                        //entityData.LogEntity();
                        break;
                    case EntityType.RADAR:
                        overSeer.ProcessRawRadarData(entityData);
                        //entityData.LogEntity();
                        break;
                }
                roundEntities[i] = entityData;
            }
            overSeer.AnalyseNewDataAndPerformUrgentSupervision(roundEntities);

            foreach(Bot bot in bots.Values)
            {
                bot.PrepareGoalForRound();
            }

            overSeer.Supervise();

            foreach(Bot bot in bots.Values)
            {
                Console.WriteLine(bot.PerformActForRound());
            }

            overSeer.ClearSingleRoundData();

            executionTimer.Stop();
            Console.Error.WriteLine($"Execution Time: {executionTimer.ElapsedMilliseconds} ms");
        }
    }
}

public class BotOverSeer
{
    #region Constants

    private const int RADAR_RANGE = 4;

    #endregion

    public Dictionary<int, Bot> Bots { get; private set; }
    public Dictionary<(int X, int Y), OreStruct> OreData { get; private set; }

    public Queue<BotActionData> botActionsPriorRound = new Queue<BotActionData>();

    private Queue<(int X, int Y)> OreWasIdentifiedAsPresentQueue = new Queue<(int X, int Y)>();
    private Queue<(int X, int Y)> DefaultQueue = new Queue<(int X, int Y)>();
    private Queue<(int X, int Y)> PossibleEnemyRadarQueue = new Queue<(int X, int Y)>();
    private Queue<EntityEnemyData> TrapEventsToProcess = new Queue<EntityEnemyData>();
    private Queue<EntityEnemyData> DigEventsToProcess = new Queue<EntityEnemyData>();

    public Dictionary<(int X, int Y), OreAssignment> OreAssignments { get; private set; } = new Dictionary<(int, int), OreAssignment>();
    public Dictionary<(int X, int Y), RadarAssignment> RadarAssignments { get; private set; } = new Dictionary<(int, int), RadarAssignment>();
    public Dictionary<(int X, int Y), TrapAssignment> TrapAssignments { get; private set; } = new Dictionary<(int, int), TrapAssignment>();
    public Dictionary<(int X, int Y), bool> EnemyTrapRecords { get; private set; } = new Dictionary<(int, int), bool>();

    private Dictionary<int, EntityEnemyData> EnemyTrackingData { get; set; } = new Dictionary<int, EntityEnemyData>();

    private List<(int X, int Y)> OreDugPriorRound { get; set; } = new List<(int, int)>();
    private List<(int X, int Y)> HolesMadePriorRound { get; set; } = new List<(int, int)>();
    public List<(int X, int Y)> RadarsActivePriorRound { get; set; } = new List<(int, int)>();
    public HashSet<(int X, int Y)> OreWasEverPresent { get; set; } = new HashSet<(int X, int Y)>();

    public int RadarCoolDown { get; set; }
    public int TrapCoolDown { get; set; }

    private bool BaseRadarNeedsMet { get; set; } = false;
    private bool IdentifiedOreLow { get; set; } = true;
    private bool OffsetRadar { get; set; } = false;
    private bool? RadarsTopDown { get; set; } = null;
    private int RoundsSinceQueueRebuild { get; set; } = 0;

    private int EnemyXMax { get; set; } = 10;
    private int? EnemyXMaxId { get; set; } = null;
    public int MapHeight { get; private set; }

    public int MapWidth { get; private set; }

    public int TurnCount { get; set; } = 0;



    public BotOverSeer(int mapHeight, int mapWidth, Dictionary<int, Bot> bots, Dictionary<(int, int), OreStruct> oreData)
    {
        this.MapHeight = mapHeight;
        this.MapWidth = mapWidth;
        this.Bots = bots;
        this.OreData = oreData;
    }

    #region Assignment

    public (int X, int Y) GetTrapAssignment(int entityId)
    {
        // Use the native ore queueing mechanism to help reduce the active queue print
        // of ore request by one to leave a remaining piece of ore for the booby trap
        (int X, int Y) key = this.GetOreAssignment(entityId, true);
        key = this.GetOreAssignment(entityId, true);

        OreStruct oreDataForBoobyTrap = this.OreData[key];
        oreDataForBoobyTrap.BombEnRoute = true;
        this.OreData[key] = oreDataForBoobyTrap;

        // use a false flag to indicate an unconfirmed trap to prevent further assignment to the vein.
        this.EnemyTrapRecords[key] = false;
        this.CancelDigAssignmentForLocation(key, 10, Utility.GetOrthoDistance(this.Bots[entityId].X, this.Bots[entityId].Y, key.X, key.Y));

        this.TrapAssignments[key] = new TrapAssignment { BotId = entityId };
        this.TrapCoolDown = 100;
        return key;
    }

    public (int X, int Y) GetOreAssignment(int entityId, bool canReorderQueue = true)
    {
        if(canReorderQueue) this.OreWasIdentifiedAsPresentQueue = new Queue<(int X, int Y)>(this.OreWasIdentifiedAsPresentQueue.OrderBy((oId) =>
        {
            int ortho = Utility.GetOrthoDistance(this.Bots[entityId].X, this.Bots[entityId].Y, oId.X, oId.Y);
            return (ortho / 3);
        }).ThenBy(oID => oID.X));
        if(this.OreWasIdentifiedAsPresentQueue.TryDequeue(out (int X, int Y) idLoc))
        {
            return this.EnemyTrapRecords.ContainsKey(idLoc)
                ? this.GetOreAssignment(entityId, false)
                : this.ProcessOreAssignment(idLoc, entityId);
        }

        if(this.TurnCount < 60)
        {
            if(this.DefaultQueue.Count == 0)
            {
                this.BuildDefaultQueue();
            }

            this.DefaultQueue = new Queue<(int X, int Y)>(this.DefaultQueue.OrderBy((oId) => Utility.GetOrthoDistance(this.Bots[entityId].X, this.Bots[entityId].Y, oId.X, oId.Y)).ThenBy(q => q.X));
            if(this.DefaultQueue.TryDequeue(out (int X, int Y) defaultLoc))
            {
                return this.EnemyTrapRecords.ContainsKey(defaultLoc)
                ? this.GetOreAssignment(entityId, false)
                : this.ProcessOreAssignment(defaultLoc, entityId);
            }
        }


        foreach(KeyValuePair<(int X, int Y), OreStruct> oreLocation in this.OreData)
        {
            if(oreLocation.Value.OreCount != 0
                && !oreLocation.Value.BombEnRoute
                && !this.OreAssignments.ContainsKey(oreLocation.Key)
                && !this.EnemyTrapRecords.ContainsKey(oreLocation.Key))
            {
                return this.ProcessOreAssignment(oreLocation.Key, entityId);
            }
        }

        foreach(KeyValuePair<(int X, int Y), OreStruct> oreLocation in this.OreData)
        {
            if(oreLocation.Value.OreCount == -1
                && !oreLocation.Value.BombEnRoute
                && !this.OreAssignments.ContainsKey(oreLocation.Key)
                && !this.EnemyTrapRecords.ContainsKey(oreLocation.Key))
            {
                return this.ProcessOreAssignment(oreLocation.Key, entityId);
            }
        }

        return (0, 5);
    }

    private void BuildDefaultQueue()
    {
        foreach(Bot b in this.Bots.Values)
        {
            this.DefaultQueue.Enqueue((3, b.Y + 1));
            this.DefaultQueue.Enqueue((3 + 1, b.Y + 2));

            this.DefaultQueue.Enqueue((7, b.Y + 1));
            this.DefaultQueue.Enqueue((7 + 1, b.Y + 2));

            this.DefaultQueue.Enqueue((11, b.Y + 1));
            this.DefaultQueue.Enqueue((11 + 1, b.Y + 2));

            this.DefaultQueue.Enqueue((15, b.Y + 1));
            this.DefaultQueue.Enqueue((15 + 1, b.Y + 2));

            this.DefaultQueue.Enqueue((19, b.Y + 1));
            this.DefaultQueue.Enqueue((19 + 1, b.Y + 2));
        }
    }

    public (int X, int Y) GetRadarSabotageAssignment(int _)
    {
        while(this.PossibleEnemyRadarQueue.TryDequeue(out (int X, int Y) radLoc))
        {
            if(!this.TrapAssignments.ContainsKey(radLoc))
            {
                return radLoc;
            }
        }
        return default;
    }

    private (int X, int Y) ProcessOreAssignment((int X, int Y) key, int entityId)
    {
        if(this.OreAssignments.ContainsKey(key))
        {
            HashSet<int> addedEntities = this.OreAssignments[key].AssignedBots;
            // if(!addedEntities.ContainsKey(entityId))
            // {
            addedEntities.Add(entityId);
            // }

            this.OreAssignments[key] = new OreAssignment
            {
                AssignedBots = addedEntities,
                AssignmentCount = this.OreAssignments[key].AssignmentCount + 1
            };
        }
        else
        {
            this.OreAssignments[key] = new OreAssignment
            {
                AssignedBots = new HashSet<int>() { entityId },
                AssignmentCount = 1
            };

        }
        return key;
    }

    private (int X, int Y) GetInitialRadarAssignmentLocation() => (3 + RADAR_RANGE / 2, 1 + (RADAR_RANGE / 2) + RADAR_RANGE);

    public (int X, int Y) GetRadarAssignment(int entityId)
    {
        if(this.RadarAssignments.Count == 0)
        {
            (int initialX, int initialY) = this.GetInitialRadarAssignmentLocation();
            this.OffsetRadar = false;
            this.RadarAssignments[(initialX, initialY)] = new RadarAssignment { BotId = entityId };
            return (initialX, initialY);
        }

        if(this.RadarAssignments.Count < 3)
        {
            if(this.OreWasEverPresent.Where(o => o.Y < (this.MapHeight / 2) && o.X < 11).Count() > 2 && !this.RadarsActivePriorRound.Any(r => r.X < 5 && r.Y < 5))
            {
                return (3, 3);
            }
            else if(this.OreWasEverPresent.Where(o => o.Y > (this.MapHeight / 2) && o.X < 11).Count() > 2 && !this.RadarsActivePriorRound.Any(r => r.X < 5 && r.Y > 9))
            {
                return (3, 11);
            }
        }


        if(this.RadarAssignments.Count == 1 && this.RadarsTopDown == null)
        {
            this.RadarsTopDown = this.OreWasEverPresent.Where(o => o.Y < (this.MapHeight / 2)).Count() > this.OreWasEverPresent.Where(o => o.Y > (this.MapHeight / 2)).Count();
        }

        KeyValuePair<(int X, int Y), RadarAssignment> lastAssignment = this.RadarAssignments.Last();
        if(this.RadarsTopDown ?? true)
        {
            if(lastAssignment.Key.Y + (2 * RADAR_RANGE) >= this.MapHeight)
            {
                if(lastAssignment.Key.X + RADAR_RANGE >= this.MapWidth)
                {
                    this.Bots[entityId].OverrideBotState(BotState.IDLE);
                    this.BaseRadarNeedsMet = true;
                    return (1, this.Bots[entityId].Y);
                }

                if(this.OffsetRadar)
                {
                    this.OffsetRadar = false;
                    int tNewColumnXOffset = lastAssignment.Key.X + RADAR_RANGE;
                    int tNewColumnYOffset = (1 + RADAR_RANGE / 2) + RADAR_RANGE;
                    this.RadarAssignments[(tNewColumnXOffset, tNewColumnYOffset)] = new RadarAssignment { BotId = entityId };
                    return (tNewColumnXOffset, tNewColumnYOffset);
                }
                this.OffsetRadar = true;
                int tNewColumnX = lastAssignment.Key.X + RADAR_RANGE;
                int tNewColumnY = 1 + RADAR_RANGE / 2;
                this.RadarAssignments[(tNewColumnX, tNewColumnY)] = new RadarAssignment { BotId = entityId };
                return (tNewColumnX, tNewColumnY);

            }

            int tNewRowX = lastAssignment.Key.X;
            int tNewRowY = lastAssignment.Key.Y + (2 * RADAR_RANGE);
            this.RadarAssignments[(tNewRowX, tNewRowY)] = new RadarAssignment { BotId = entityId };
            return (tNewRowX, tNewRowY);
        }
        else
        {
            if(lastAssignment.Key.Y - (2 * RADAR_RANGE) < 0)
            {
                if(lastAssignment.Key.X + RADAR_RANGE >= this.MapWidth)
                {
                    this.Bots[entityId].OverrideBotState(BotState.IDLE);
                    this.BaseRadarNeedsMet = true;
                    return (1, this.Bots[entityId].Y);
                }

                if(this.OffsetRadar)
                {
                    this.OffsetRadar = false;
                    int tNewColumnXOffset = lastAssignment.Key.X + RADAR_RANGE;
                    int tNewColumnYOffset = (1 + RADAR_RANGE / 2) + RADAR_RANGE;
                    this.RadarAssignments[(tNewColumnXOffset, tNewColumnYOffset)] = new RadarAssignment { BotId = entityId };
                    return (tNewColumnXOffset, tNewColumnYOffset);
                }
                this.OffsetRadar = true;
                int tNewColumnX = lastAssignment.Key.X + RADAR_RANGE;
                int tNewColumnY = this.MapHeight - 1 - (RADAR_RANGE / 2);
                this.RadarAssignments[(tNewColumnX, tNewColumnY)] = new RadarAssignment { BotId = entityId };
                return (tNewColumnX, tNewColumnY);

            }

            int tNewRowX = lastAssignment.Key.X;
            int tNewRowY = lastAssignment.Key.Y - (2 * RADAR_RANGE);
            this.RadarAssignments[(tNewRowX, tNewRowY)] = new RadarAssignment { BotId = entityId };
            return (tNewRowX, tNewRowY);
        }


    }
    #endregion

    public void AnalyseNewDataAndPerformUrgentSupervision(EntityData[] entities)
    {
        this.ProcessFriendlyBotDeath(entities);
        this.ProcessBotActionsPriorRound();
        this.ProcessFirstPassDigEventsForPriorRound();
        this.ProcessFirstPassTrapEventsForPriorRound();
        this.ProcessDigEventsForPriorRound();
        this.ProcessTrapEventsForPriorRound();

        bool friendRadarOverrideUsed = this.NeedRadarOverride() && this.HandleRadarOverride();
        bool enemyRadarOverrideUsed = this.NeedEnemyRadarOverride() && this.HandleEnemyRadarOverride();
        bool sackModeUsed = this.ShouldSackABot();
    }

    public void Supervise()
    {

        bool oreQueueRebuildUsed = this.NeedOreIdQueueRebuilt() && this.RebuildOreIdentifiedQueue();

        this.CheckForOreAssignmentOptimization();
        this.CheckForEnemyLikelyToStealOre();

        this.LogDiagnostics();
        this.TurnCount++;
    }

    public bool ProcessRawRadarData(EntityData entityData)
    {
        this.RadarsActivePriorRound.Add((entityData.X, entityData.Y));
        return true;
    }

    public bool ProcessRawOreStruct((int X, int Y) key, OreStruct oreStruct)
    {
        if(!this.OreData.ContainsKey(key))
        {
            this.OreData[key] = oreStruct;
            if(oreStruct.HolePresent == false)
            {
                this.EnemyTrapRecords.Remove(key);
                for(int i = 0; i < oreStruct.OreCount; i++)
                {
                    this.OreWasIdentifiedAsPresentQueue.Enqueue(key);
                }
            }
            return true;
        }


        OreStruct currentOreStruct = this.OreData[key];
        oreStruct.IsUsedForCache = currentOreStruct.IsUsedForCache;
        oreStruct.BombEnRoute = currentOreStruct.BombEnRoute;
        

        // If there is not a hole there is not a bomb.
        if(oreStruct.HolePresent == false)
        {
            this.EnemyTrapRecords.Remove(key);
        }

        // If we have a 0 and the input reports -1 we have identified this spot as empty without a radar
        if(currentOreStruct.OreCount != 0 && oreStruct.OreCount == -1)
        {
            return false;
        }

        if(currentOreStruct.HolePresent != oreStruct.HolePresent)
        {
            this.HolesMadePriorRound.Add(key);
        }

        if(oreStruct.OreCount < currentOreStruct.OreCount)
        {
            for(int i = 0; i < currentOreStruct.OreCount - oreStruct.OreCount; i++)
            {
                this.OreDugPriorRound.Add(key);
                this.EnemyTrapRecords.Remove(key);
                for(int j = 0; j < oreStruct.OreCount; j++)
                {
                    this.OreWasIdentifiedAsPresentQueue.Enqueue(key);
                }
            }
        }

        if(oreStruct.OreCount > 0 && currentOreStruct.HasBeenIdentified == false)
        {
            this.OreWasEverPresent.Add(key);

            oreStruct.HasBeenIdentified = true;
            this.OreData[key] = oreStruct;

            int outStandingOreDispatches = this.OreAssignments.TryGetValue(key, out OreAssignment storedOreAssignmentData) ? storedOreAssignmentData.AssignmentCount : 0;

            for(int i = 0; i < oreStruct.OreCount - outStandingOreDispatches; i++)
            {
                this.OreWasIdentifiedAsPresentQueue.Enqueue(key);
            }
            return true;
        }

        this.OreData[key] = oreStruct;
        return true;
    }

    // Processes Raw Enemy Feed from the underlying native reporting system. If the enemy is of note returns true
    public bool ProcessRawEnemyData(EntityData entity)
    {
        if(!this.EnemyTrackingData.ContainsKey(entity.EntityId))
        {
            this.EnemyTrackingData[entity.EntityId] = new EntityEnemyData(entity);
            return true;
        }

        EntityEnemyData priorEnemyData = this.EnemyTrackingData[entity.EntityId];
        EntityEnemyData newEnemyData = new EntityEnemyData(entity, priorEnemyData.ItemSuspect, priorEnemyData.WaitCount);

        if(priorEnemyData.X == newEnemyData.X && priorEnemyData.Y == newEnemyData.Y)
        {
            if(entity.X > this.EnemyXMax)
            {
                if(entity.X > this.EnemyXMax + RADAR_RANGE + 1)
                {
                    this.EnemyXMaxId = entity.EntityId;
                }
                this.EnemyXMax = entity.X;
            }

            if((++newEnemyData.WaitCount > 0 || newEnemyData.ItemSuspect == ItemType.NONE) && newEnemyData.X == 0)
            {
                // Enemy requested an item.
                Console.Error.WriteLine($"Enemy at ({newEnemyData.X},{newEnemyData.Y} suspected of carrying trap)");
                newEnemyData.ItemSuspect = ItemType.TRAP;
                this.EnemyTrackingData[entity.EntityId] = newEnemyData;
                return true;
            }
            else if(newEnemyData.X != 0 && newEnemyData.ItemSuspect == ItemType.TRAP)
            {
                newEnemyData.ItemSuspect = ItemType.NONE;
                if(entity.EntityId == (this.EnemyXMaxId ?? -1))
                {
                    this.EnemyXMaxId = null;
                    this.ProcessLikelyEnemyRadarPlant((entity.X, entity.Y));
                }
                else
                {
                    this.TrapEventsToProcess.Enqueue(newEnemyData);
                    this.EnemyTrackingData[entity.EntityId] = newEnemyData;
                    return true;
                }
            }
            else if(newEnemyData.X != 0 && newEnemyData.ItemSuspect == ItemType.NONE)
            {
                newEnemyData.ItemSuspect = ItemType.ORE;
                this.DigEventsToProcess.Enqueue(newEnemyData);
                this.EnemyTrackingData[entity.EntityId] = newEnemyData;
                return true;
            }
            else if(newEnemyData.X == 0 && newEnemyData.ItemSuspect == ItemType.ORE)
            {
                newEnemyData.ItemSuspect = ItemType.NONE;
                this.EnemyTrackingData[entity.EntityId] = newEnemyData;
                return true;
            }

        }
        else
        {
            newEnemyData.WaitCount = 0;
        }

        this.EnemyTrackingData[entity.EntityId] = newEnemyData;
        return false;
    }

    private void ProcessLikelyEnemyRadarPlant((int X, int Y) key)
    {
        (int X, int Y) keyForRadar = (key.X, key.Y);
        if(!this.TrapAssignments.ContainsKey(keyForRadar) && key.Y > 0 && key.Y < this.MapHeight && key.X < this.MapWidth)
        {
            this.PossibleEnemyRadarQueue.Enqueue(keyForRadar);
        }

        keyForRadar = (key.X + 1, key.Y);
        if(!this.TrapAssignments.ContainsKey(keyForRadar) && key.Y > 0 && key.Y < this.MapHeight && key.X < this.MapWidth)
        {
            this.PossibleEnemyRadarQueue.Enqueue(keyForRadar);
        }

        keyForRadar = (key.X, key.Y + 1);
        if(!this.TrapAssignments.ContainsKey(keyForRadar) && key.Y > 0 && key.Y < this.MapHeight && key.X < this.MapWidth)
        {
            this.PossibleEnemyRadarQueue.Enqueue(keyForRadar);
        }

        keyForRadar = (key.X, key.Y - 1);
        if(!this.TrapAssignments.ContainsKey(keyForRadar) && key.Y > 0 && key.Y < this.MapHeight && key.X < this.MapWidth)
        {
            this.PossibleEnemyRadarQueue.Enqueue(keyForRadar);
        }

        keyForRadar = (key.X - 1, key.Y);
        if(!this.TrapAssignments.ContainsKey(keyForRadar) && key.Y > 0 && key.Y < this.MapHeight && key.X < this.MapWidth)
        {
            this.PossibleEnemyRadarQueue.Enqueue(keyForRadar);
        }
    }

    private void ProcessOreDigEvent(EntityEnemyData entityDigging)
    {
        List<(int X, int Y)> identifiedDigLocations
            = this.OreDugPriorRound
                .Where((od) => Utility.GetOrthoDistance(entityDigging.X, entityDigging.Y, od.X, od.Y) <= 1)
                .ToList();
        if(identifiedDigLocations.Count == 1)
        {
            (int X, int Y) specificKey = (identifiedDigLocations[0].X, identifiedDigLocations[0].Y);
            this.CancelDigAssignmentForLocation(specificKey, 1);
            this.OreDugPriorRound.Remove(specificKey);
            return;

        }

        (int X, int Y) key = (entityDigging.X, entityDigging.Y);
        if(identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y))
        {
            this.CancelDigAssignmentForLocation(key, 1);
        }

        key = (entityDigging.X + 1, entityDigging.Y);
        if(identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y))
        {
            this.CancelDigAssignmentForLocation(key, 1);
        }

        key = (entityDigging.X, entityDigging.Y + 1);
        if(identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y))
        {
            this.CancelDigAssignmentForLocation(key, 1);
        }

        key = (entityDigging.X - 1, entityDigging.Y);
        if(identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y))
        {
            this.CancelDigAssignmentForLocation(key, 1);
        }


        key = (entityDigging.X, entityDigging.Y - 1);
        if(identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y))
        {
            this.CancelDigAssignmentForLocation(key, 1);
        }
    }

    public void ProcessFriendlyTrapEvent((int X, int Y) key)
    {
        this.EnemyTrapRecords[key] = true;
        OreStruct currentOreData = this.OreData[key];
        currentOreData.OreCount = -2;
        this.OreData[key] = currentOreData;
        this.CancelDigAssignmentForLocation(key);
    }

    private void ProcessTrapPlantEvent(EntityEnemyData entityDataAtTimeOfTrapDrop)
    {


        Console.Error.WriteLine($"Processing Trap drop event of Entity at ({entityDataAtTimeOfTrapDrop.X},{entityDataAtTimeOfTrapDrop.Y})");
        if(this.RadarsActivePriorRound.Any(r => Utility.GetOrthoDistance(entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y, r.X, r.Y) < RADAR_RANGE))
        {
            List<(int X, int Y)> identifiedDigLocations
                = this.OreDugPriorRound
                    .Where((od) => Utility.GetOrthoDistance(entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y, od.X, od.Y) <= 1)
                    .Distinct()
                    .ToList();

            Console.Error.Write("Identified Ore Dug Locations at ");
            foreach((int X, int Y) dugLoc in identifiedDigLocations)
            {
                Console.Error.Write($"({dugLoc.X},{dugLoc.Y})");
            }
            Console.Error.WriteLine();
            if(identifiedDigLocations.Count == 1)
            {
                Console.Error.WriteLine($" determined trap drop at ({identifiedDigLocations[0].X},{identifiedDigLocations[0].Y})");
                (int X, int Y) specificKey = (identifiedDigLocations[0].X, identifiedDigLocations[0].Y);
                this.EnemyTrapRecords[specificKey] = true;
                this.CancelDigAssignmentForLocation(specificKey);
                this.OreDugPriorRound.Remove(specificKey);
                this.RebuildOreIdentifiedQueue();
                return;
            }

            // Check if we can identify the trap by the holes dug last round.
            // TODO there are a few more optimizations that could be done such as winnowowing out other nearby digs.
            List<(int X, int Y)> holesDugPriorRoundAdjacent = this.HolesMadePriorRound.Where(hole => Utility.GetOrthoDistance(entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y, hole.X, hole.Y) <= 1).ToList();
            Console.Error.Write("Identified Hole Dug Locations at ");
            foreach((int X, int Y) dugLoc in holesDugPriorRoundAdjacent)
            {
                Console.Error.Write($"({dugLoc.X},{dugLoc.Y})");
            }
            Console.Error.WriteLine();
            foreach((int X, int Y) holeLoc in holesDugPriorRoundAdjacent)
            {
                Console.Error.WriteLine($"Processing Hole At ({holeLoc.X},{holeLoc.Y})");
                List<EntityEnemyData> enemyBotsInRange = this.EnemyTrackingData.Values.Where(enemyBot => Utility.GetOrthoDistance(holeLoc.X, holeLoc.Y, enemyBot.X, enemyBot.Y) <= 1).ToList();
                Console.Error.WriteLine($"enemyBots: {enemyBotsInRange.Count}");

                if(enemyBotsInRange.Count == 1)
                {
                    Console.Error.WriteLine($" determined trap drop by holes at ({holeLoc.X},{holeLoc.Y})");
                    (int X, int Y) specificHoleKey = (holeLoc.X, holeLoc.Y);
                    this.EnemyTrapRecords[specificHoleKey] = true;
                    this.CancelDigAssignmentForLocation(specificHoleKey);
                    this.OreDugPriorRound.Remove(specificHoleKey);
                    this.RebuildOreIdentifiedQueue();
                    return;
                }
            }

            // Because we can't identify where the trap was placed, we have consider flagging all adjacent areas.
            (int X, int Y) key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y);
            bool haveOreData = this.OreData.TryGetValue(key, out OreStruct oreData);
            if((identifiedDigLocations.Count == 0 || identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y)) && (!haveOreData || oreData.HolePresent || !oreData.HasBeenIdentified))
            {
                Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
                this.EnemyTrapRecords[key]
                    = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
                this.CancelDigAssignmentForLocation(key);
            }


            key = (entityDataAtTimeOfTrapDrop.X + 1, entityDataAtTimeOfTrapDrop.Y);
            haveOreData = this.OreData.TryGetValue(key, out oreData);
            if((identifiedDigLocations.Count == 0 || identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y)) && (!haveOreData || oreData.HolePresent || !oreData.HasBeenIdentified))
            {
                Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
                this.EnemyTrapRecords[key]
                    = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
                this.CancelDigAssignmentForLocation(key);
            }

            key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y + 1);
            haveOreData = this.OreData.TryGetValue(key, out oreData);
            if((identifiedDigLocations.Count == 0 || identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y)) && (!haveOreData || oreData.HolePresent || !oreData.HasBeenIdentified))
            {
                Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
                this.EnemyTrapRecords[key]
                    = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
                this.CancelDigAssignmentForLocation(key);
            }

            key = (entityDataAtTimeOfTrapDrop.X - 1, entityDataAtTimeOfTrapDrop.Y);
            haveOreData = this.OreData.TryGetValue(key, out oreData);
            if((identifiedDigLocations.Count == 0 || identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y)) && (!haveOreData || oreData.HolePresent || !oreData.HasBeenIdentified))
            {
                Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
                this.EnemyTrapRecords[key]
                    = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
                this.CancelDigAssignmentForLocation(key);
            }

            key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y - 1);
            haveOreData = this.OreData.TryGetValue(key, out oreData);
            if((identifiedDigLocations.Count == 0 || identifiedDigLocations.Any((od) => od.X == key.X && od.Y == key.Y)) && (!haveOreData || oreData.HolePresent || oreData.HasBeenIdentified == false))
            {
                Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
                this.EnemyTrapRecords[key]
                    = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
                this.CancelDigAssignmentForLocation(key);
            }
        }
        else
        {
            Console.Error.WriteLine("No Radars in Range for Trap Drop Processing");
            // Because we can't identify where the trap was placed, we have to flag all adjacent areas.
            (int X, int Y) key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y);
            Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
            this.EnemyTrapRecords[key] = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
            this.CancelDigAssignmentForLocation(key);

            key = (entityDataAtTimeOfTrapDrop.X + 1, entityDataAtTimeOfTrapDrop.Y);
            Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
            this.EnemyTrapRecords[key] = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
            this.CancelDigAssignmentForLocation(key);

            key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y + 1);
            Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
            this.EnemyTrapRecords[key] = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
            this.CancelDigAssignmentForLocation(key);

            key = (entityDataAtTimeOfTrapDrop.X - 1, entityDataAtTimeOfTrapDrop.Y);
            Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
            this.EnemyTrapRecords[key] = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
            this.CancelDigAssignmentForLocation(key);

            key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y - 1);
            Console.Error.WriteLine($" cautioning of trap drop at ({key.X},{key.Y})");
            this.EnemyTrapRecords[key] = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
            this.CancelDigAssignmentForLocation(key);

        }

        this.RebuildOreIdentifiedQueue();
    }

    private void ProcessFriendlyBotDeath(EntityData[] entities)
    {
        Dictionary<int, bool> aliveBots = new Dictionary<int, bool>();
        foreach(EntityData entity in entities)
        {
            if(entity.X == -1 && entity.Y == -1)
            {
                _ = this.Bots.TryGetValue(entity.EntityId, out Bot bot) && bot.State != BotState.DEAD && bot.OverrideBotState(BotState.DEAD);
            }
        }
    }

    private void ProcessBotActionsPriorRound()
    {
        foreach(BotActionData act in this.botActionsPriorRound)
        {
            switch(act.Action)
            {
                case BotAct.WAIT:
                    break;

                case BotAct.MOVE:
                    break;

                case BotAct.DIG:
                    if(act.Item == ItemType.RADAR)
                    {
                        this.RadarsActivePriorRound.Add((act.X, act.Y));
                    }
                    this.OreDugPriorRound.Remove((act.X, act.Y));
                    this.HolesMadePriorRound.Remove((act.X, act.Y));
                    break;

                case BotAct.REQUEST:
                    break;
            }
        }
    }

    private void ProcessFirstPassDigEventsForPriorRound()
    {

        
        for(int i = 0; i < this.DigEventsToProcess.Count; i++)
        {
            EntityEnemyData newEnemyData = this.DigEventsToProcess.Dequeue();
            bool earlyEvalSuccess = false;
            foreach((int X, int Y) key in this.OreDugPriorRound.Where(o => Utility.GetOrthoDistance(o, newEnemyData.X, newEnemyData.Y) <= 1))
            {
                if(!earlyEvalSuccess && this.EnemyTrackingData.Values.Where(newEnemyData => Utility.GetOrthoDistance(key, newEnemyData.X, newEnemyData.Y) <= 1).Count() == 1)
                {
                    (int X, int Y) specificKey = (key.X, key.Y);
                    this.CancelDigAssignmentForLocation(specificKey, 1);
                    this.OreDugPriorRound.Remove(key);
                    earlyEvalSuccess = true;
                    break;
                }
            }
            if(!earlyEvalSuccess)
            {
                this.DigEventsToProcess.Enqueue(newEnemyData);
            }
        }
    }



    private void ProcessFirstPassTrapEventsForPriorRound()
    {
        for(int i = 0; i < this.TrapEventsToProcess.Count; i++)
        {
            EntityEnemyData newEnemyData = this.TrapEventsToProcess.Dequeue();
            bool earlyEvalSuccess = false;
            foreach((int X, int Y) key in this.OreDugPriorRound.Where(o => Utility.GetOrthoDistance(o, newEnemyData.X, newEnemyData.Y) <= 1))
            {
                if(!earlyEvalSuccess && this.EnemyTrackingData.Values.Where(newEnemyData => Utility.GetOrthoDistance(key, newEnemyData.X, newEnemyData.Y) <= 1).Count() == 1)
                {
                    Console.Error.WriteLine($" determined early trap drop event at ({key.X},{key.Y})");
                    (int X, int Y) specificKey = (key.X, key.Y);
                    this.EnemyTrapRecords[specificKey] = true;
                    this.OreDugPriorRound.Remove(key);
                    this.CancelDigAssignmentForLocation(specificKey);
                    this.RebuildOreIdentifiedQueue();
                    earlyEvalSuccess = true;
                    break;
                }
            }
            if(!earlyEvalSuccess)
            {
                this.TrapEventsToProcess.Enqueue(newEnemyData);
            }
        }
    }
    private void ProcessDigEventsForPriorRound()
    {
        while(this.DigEventsToProcess.Count > 0)
        {
            EntityEnemyData newEnemyData = this.DigEventsToProcess.Dequeue();
            this.ProcessOreDigEvent(newEnemyData);
        }
    }

    private void ProcessTrapEventsForPriorRound()
    {
        while(this.TrapEventsToProcess.Count > 0)
        {
            EntityEnemyData newEnemyData = this.TrapEventsToProcess.Dequeue();
            this.ProcessTrapPlantEvent(newEnemyData);
        }
    }

    private void CancelDigAssignmentForLocation((int X, int Y) key, int maxToCancel = 10, int orthDistanceForDigToBeat = 0)
    {
        this.CancelOreAssignmentForLocation(key, maxToCancel, orthDistanceForDigToBeat);
        this.CancelRadarAssignmentForLocation(key);
        this.CancelTrapAssignmentForLocation(key);
        this.CancelRadarSabotageAssignmentForLocation(key);
    }

    private void CancelOreAssignmentForLocation((int X, int Y) key, int maxToCancel, int orthDistanceForDigToBeat)
    {
        // Clear the Ore ID Queue for Location
        for(int i = 0; i < this.OreWasIdentifiedAsPresentQueue.Count; i++)
        {
            (int X, int Y) radarLoc = this.OreWasIdentifiedAsPresentQueue.Dequeue();
            if(radarLoc.X != key.X || radarLoc.Y != key.Y)
            {
                this.OreWasIdentifiedAsPresentQueue.Enqueue(radarLoc);
            }
        }

        List<(int orthoDistance, Bot bot)> botsEligibleForCancel = new List<(int, Bot)>();

        int cancelCount = 0;

        foreach(Bot botToPotentiallyRecall in this.Bots.Values)
        {
            if(botToPotentiallyRecall.Tx == key.X && botToPotentiallyRecall.Ty == key.Y)
            {
                int orthoDistance = Utility.GetOrthoDistance(botToPotentiallyRecall.X, botToPotentiallyRecall.Y, key.X, key.Y);
                if(orthoDistance >= orthDistanceForDigToBeat)
                {
                    botsEligibleForCancel.Add((orthoDistance, botToPotentiallyRecall));

                }
            }
        }

        foreach((int orthoDistance, Bot bot) in botsEligibleForCancel.OrderBy(botNDistance => -botNDistance.orthoDistance))
        {
            bot.OverrideBotState(BotState.IDLE);
            if(++cancelCount >= maxToCancel)
            {
                return;
            }
        }
    }

    private void CancelRadarAssignmentForLocation((int X, int Y) key)
    {
        if(this.RadarAssignments.TryGetValue(key, out RadarAssignment radarAssignment))
        {
            Bot botToPotentiallyRecall = this.Bots[radarAssignment.BotId];
            if(botToPotentiallyRecall.Tx == key.X && botToPotentiallyRecall.Ty == key.Y)
            {
                botToPotentiallyRecall.OverrideBotState(BotState.IDLE);
            }
        }
    }

    private void CancelTrapAssignmentForLocation((int X, int Y) key)
    {
        if(this.TrapAssignments.TryGetValue(key, out TrapAssignment trapAssignment))
        {
            Bot botToPotentiallyRecall = this.Bots[trapAssignment.BotId];
            if(botToPotentiallyRecall.Tx == key.X && botToPotentiallyRecall.Ty == key.Y)
            {
                botToPotentiallyRecall.OverrideBotState(BotState.IDLE);
            }
        }
    }

    private void CancelRadarSabotageAssignmentForLocation((int X, int Y) key)
    {
        for(int i = 0; i < this.PossibleEnemyRadarQueue.Count; i++)
        {
            (int X, int Y) radarLoc = this.PossibleEnemyRadarQueue.Dequeue();
            if(radarLoc.X != key.X || radarLoc.Y != key.Y)
            {
                this.PossibleEnemyRadarQueue.Enqueue(radarLoc);
            }
        }

        foreach(Bot bot in this.Bots.Values.Where(b => b.State == BotState.RADAR_SABOTAGE))
        {
            if(bot.Tx == key.X && bot.Ty == key.Y)
            {
                bot.OverrideBotState(BotState.IDLE);
            }
        }

    }
    private void CheckForOreAssignmentOptimization()
    {
        Bot[] botsEnrouteToOre = this.Bots.Values.Where(b => b.State == BotState.FETCHING).OrderBy(b => b.X + b.Y).ToArray();

        if(botsEnrouteToOre.Length > 0)
        {
            List<Bot> botsWhoSwitchedPositions = new List<Bot>();
            (int Tx, int Ty)[] oreDestinations = botsEnrouteToOre.OrderBy(b => b.Tx + b.Ty).Select(b => (b.Tx, b.Ty)).ToArray();
            for(int i = 0; i < botsEnrouteToOre.Length; i++)
            {
                (int Tx, int Ty) = (botsEnrouteToOre[i].Tx, botsEnrouteToOre[i].Ty);

                botsEnrouteToOre[i].Tx = oreDestinations[i].Tx;
                botsEnrouteToOre[i].Ty = oreDestinations[i].Ty;

                if(botsEnrouteToOre[i].Tx != Tx || botsEnrouteToOre[i].Ty != Ty)
                {
                    botsWhoSwitchedPositions.Add(botsEnrouteToOre[i]);
                    if(this.OreAssignments.TryGetValue((Tx, Ty), out OreAssignment ore)) ore.AssignedBots.Remove(botsEnrouteToOre[i].EntityId);
                    if(this.OreAssignments.TryGetValue((botsEnrouteToOre[i].Tx, botsEnrouteToOre[i].Ty), out ore)) ore.AssignedBots.Add(botsEnrouteToOre[i].EntityId);
                }
            }

            Console.Error.WriteLine($"Optimized ore locations for {botsWhoSwitchedPositions.Count} bots");

            foreach(Bot bot in botsWhoSwitchedPositions)
            {
                int currentTravelCost = Utility.GetOrthoDistanceTurnCost(bot.X, bot.Y, bot.Tx, bot.Ty);

                if(this.OreWasIdentifiedAsPresentQueue.Any(o => Utility.GetOrthoDistanceTurnCost(bot.X, bot.Y, o.X, o.Y) < currentTravelCost))
                {
                    if(this.OreAssignments.TryGetValue((bot.Tx, bot.Ty), out OreAssignment ore)) ore.AssignedBots.Remove(bot.EntityId);
                    this.OreWasIdentifiedAsPresentQueue.Enqueue((bot.Tx, bot.Ty));

                    (bot.Tx, bot.Ty) = this.GetOreAssignment(bot.EntityId);
                }
            }

        }


    }

    private void CheckForEnemyLikelyToStealOre()
    {
        foreach(IGrouping<(int Tx, int Ty), Bot>? botGroup in this.Bots.Values.Where(bot => bot.State == BotState.FETCHING).GroupBy(bot => (bot.Tx, bot.Ty)))
        {
            if(botGroup != null && this.OreData.TryGetValue(botGroup.Key, out OreStruct ore) && ore.HasBeenIdentified)
            {
                List<int> orthoEnemyTurnCounts = new List<int>();

                foreach(EntityEnemyData enemyBot in this.EnemyTrackingData.Values)
                {
                    int orthoTurnCount = Utility.GetOrthoDistanceTurnCost(enemyBot.X, enemyBot.Y, botGroup.Key.Tx, botGroup.Key.Ty);
                    orthoEnemyTurnCounts.Add(Utility.GetOrthoDistanceTurnCost(enemyBot.X, enemyBot.Y, botGroup.Key.Tx, botGroup.Key.Ty));
                    //Console.Error.WriteLine($"Ortho Turn Count for enemy bot ({enemyBot.X},{enemyBot.Y}) to ({botGroup.Key.Tx},{botGroup.Key.Ty}) is {orthoTurnCount}");
                }

                List<int> orthoFriendlyTurnCounts = new List<int>();
                List<Bot> friendBotArray = new List<Bot>();
                foreach(Bot bot in friendBotArray)
                {
                    int orthoTurnCount = Utility.GetOrthoDistanceTurnCost(bot.X, bot.Y, bot.Tx, bot.Ty);
                    if(orthoTurnCount <= 2)
                    {
                        friendBotArray.Add(bot);
                        orthoFriendlyTurnCounts.Add(orthoTurnCount);
                        //Console.Error.WriteLine($"Ortho Turn Count for ({bot.X},{bot.Y}) to ({bot.Tx},{bot.Ty}) is {orthoTurnCount}");
                    }
                }

                orthoEnemyTurnCounts.Sort();
                orthoFriendlyTurnCounts.Sort();

                int oreRemaining = ore.OreCount;
                int i = 0; int j = 0;
                while(i < orthoEnemyTurnCounts.Count && j < orthoFriendlyTurnCounts.Count)
                {
                    if(orthoEnemyTurnCounts[i] < orthoFriendlyTurnCounts[j])
                    {
                        oreRemaining--;
                        i++;
                        j++;
                    }
                    else
                    {
                        j++;
                    }
                }

                int botsToRemove = friendBotArray.Count - oreRemaining;

                for(int k = 0; k < botsToRemove && k < friendBotArray.Count; k++)
                {
                    Bot bot = botGroup.ElementAt(k);
                    this.OreAssignments[botGroup.Key].AssignedBots.Remove(bot.EntityId);
                    (bot.Tx, bot.Ty) = this.GetOreAssignment(bot.EntityId);
                    Console.Error.WriteLine($"Reassigning {bot.EntityId} from ({botGroup.Key.Tx},{botGroup.Key.Ty}) to ({bot.Tx},{bot.Ty}) due to lost race");
                }
            }
        }
    }



    private void LogDiagnostics()
    {
        //Console.Error.Write("Dug Prior Records");
        //int printCount = 0;
        //foreach((int X, int Y) key in this.OreDugPriorRound)
        //{
        //    if(++printCount > 5)
        //    {
        //        printCount = 0;
        //        Console.Error.WriteLine($"(   {key.X}, {key.Y})");
        //    }
        //    else
        //    {
        //        Console.Error.Write($"| ({key.X}, {key.Y})");
        //    }
        //}
        //Console.Error.WriteLine();

        EntityEnemyData[] enemies = this.EnemyTrackingData.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToArray();
        for(int i = 0; i < enemies.Length; i++)
        {
            enemies[i].LogEnemyEntity();
        }

        //Console.Error.WriteLine("Radars Active at:");
        //foreach(var radarLoc in this.RadarsActivePriorRound)
        //{
        //    Console.Error.WriteLine($"({radarLoc.X},{radarLoc.Y})");
        //}

        // Console.Error.Write("Trap Records");
        // printCount = 0;
        // foreach((int X, int Y) key in this.EnemyTrapRecords.Keys)
        // {
        //     if(++printCount > 5)
        //     {
        //         printCount = 0;
        //         Console.Error.WriteLine($"(   {key.X}, {key.Y})");
        //     }
        //     else
        //     {
        //         Console.Error.Write($"| ({key.X}, {key.Y})");
        //     }
        // }
        // Console.Error.WriteLine();


        //int printCount = 0;
        //(int x, int y) priorKey = (-1, -1);
        //Console.Error.Write("Ore Cache Queue | ");
        //for (int i = 0; i < this.CacheQueue.Count; i++)
        //{
        //    if(this.CacheQueue.TryDequeue(out (int x, int y) oreIdentifiedLocation))
        //    {
        //        if (oreIdentifiedLocation.x == priorKey.x && oreIdentifiedLocation.y == priorKey.y)
        //        {
        //            Console.Error.Write("+");
        //        }
        //        else
        //        {
        //            if (++printCount > 5)
        //            {
        //                printCount = 0;
        //                Console.Error.WriteLine($" ({oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
        //            }
        //            else
        //            {
        //                Console.Error.Write($"| ({oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
        //            }
        //        }
        //        priorKey = oreIdentifiedLocation;
        //        this.CacheQueue.Enqueue(oreIdentifiedLocation);
        //    }

        //}
        //Console.Error.WriteLine();

        for(int j = 0; j < this.MapHeight; j++)
        {
            for(int i = 0; i < this.MapWidth; i++)
            {
                Console.Error.Write(
                    this.EnemyTrapRecords.TryGetValue((i, j), out bool isTrap)
                        ? isTrap
                            ? "X"
                            : "/"
                        : this.OreData.TryGetValue((i, j), out OreStruct ore)
                            ? ore.IsUsedForCache
                                ? "C"
                                : ore.OreCount == -1
                                    ? "_"
                                    : ore.OreCount.ToString()
                            : "?");
                Console.Error.Write(" ");
            }
            Console.Error.WriteLine();
        }

        //int printCount = 0;
        //(int x, int y) priorKey = (-1, -1);
        //Console.Error.Write("Ore Identified Queue | ");
        //for (int i = 0; i < this.OreWasIdentifiedAsPresentQueue.Count; i++)
        //{
        //    (int x, int y) oreIdentifiedLocation = this.OreWasIdentifiedAsPresentQueue.Dequeue();
        //    if (oreIdentifiedLocation.x == priorKey.x && oreIdentifiedLocation.y == priorKey.y)
        //    {
        //        Console.Error.Write("+");
        //    }
        //    else
        //    {
        //        if (++printCount > 5)
        //        {
        //            printCount = 0;
        //            Console.Error.WriteLine($" ({oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
        //        }
        //        else
        //        {
        //            Console.Error.Write($"| ({oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
        //        }
        //    }
        //    priorKey = oreIdentifiedLocation;
        //    this.OreWasIdentifiedAsPresentQueue.Enqueue(oreIdentifiedLocation);
        //}
        //Console.Error.WriteLine();
    }

    public void ClearSingleRoundData()
    {
        this.OreDugPriorRound = new List<(int X, int Y)>();
        this.HolesMadePriorRound = new List<(int X, int Y)>();
        this.RadarsActivePriorRound = new List<(int X, int Y)>() { };
    }

    private bool NeedOreIdQueueRebuilt() => ++this.RoundsSinceQueueRebuild > 2;
    private bool RebuildOreIdentifiedQueue()
    {

        this.RoundsSinceQueueRebuild = 0;
        this.OreWasIdentifiedAsPresentQueue.Clear();

        foreach(KeyValuePair<(int X, int Y), OreStruct> kvp in this.OreData.OrderBy(kvp => kvp.Key.X))
        {
            int potentialOreAssignment = kvp.Value.OreCount - this.Bots.Values.Where(b => b.Tx == kvp.Key.X && b.Ty == kvp.Key.Y).Count();
            if(potentialOreAssignment > 0)
            {
                if(!this.TrapAssignments.ContainsKey(kvp.Key) && (!this.OreData[kvp.Key].IsUsedForCache || this.TurnCount > 50))
                {
                    for(int i = 0; i < potentialOreAssignment; i++)
                    {
                        this.OreWasIdentifiedAsPresentQueue.Enqueue(kvp.Key);
                    }
                }
            }
            if(this.OreWasIdentifiedAsPresentQueue.Count > 50)
            {
                this.IdentifiedOreLow = false;
                return true;
            }
        }
        if(this.TurnCount > 50 && this.OreWasIdentifiedAsPresentQueue.Count < this.Bots.Values.Where(b => b.State != BotState.DEAD).Count())
        {
            this.ClearOreLocationsWithOnlyUndeterminedBombPresence();
        }
        this.IdentifiedOreLow = true;
        return true;
    }

    private void ClearOreLocationsWithOnlyUndeterminedBombPresence()
    {
        this.EnemyTrapRecords = this.EnemyTrapRecords.Where(KeyValuePair => KeyValuePair.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
    public bool ShouldWaitForRadar(Bot bot)
    {
        if(!this.Bots.Values.Any(bot => bot.State == BotState.RADAR_RETRIEVE) && this.RadarAssignments.Count() < 9 && this.IdentifiedOreLow)
        {
            (Bot nextClosestBot, int turnCount) = this.TurnCountToNextBotDeposit(false, new[] { bot });

            return turnCount - 3 < this.RadarCoolDown;
        }   
        return false; 
    }

    public (Bot bot, int turnCount) TurnCountToNextBotDeposit(bool includeZeroCost = true, Bot[] botsToExclude = null)
    {
        Bot nextClosestBot = null;
        int minTurnCount = 50;
        botsToExclude ??= Array.Empty<Bot>();
        
        foreach(Bot bot in this.Bots.Values)
        {
            if(!botsToExclude.Contains(bot))
            {
                int turnCountForThisBot = bot.State switch
                {
                    BotState.DEPOSITING => ReturnTime(bot),
                    BotState.DIGGING => ReturnTime(bot) + DigTime(bot),
                    BotState.FETCHING => ReturnTime(bot) + DigTime(bot) + FetchTime(bot),
                    _ => 100
                };

                if(minTurnCount > turnCountForThisBot && (turnCountForThisBot != 0 || !includeZeroCost))
                {
                    nextClosestBot = bot;
                    minTurnCount = turnCountForThisBot;
                }
            }
        }

        return (nextClosestBot ?? botsToExclude.FirstOrDefault(), minTurnCount);

        int ReturnTime(Bot bot) => bot.X == 0 ? 0 : (bot.X / 3) + 1;
        int DigTime(Bot bot) => 1;
        int FetchTime(Bot bot) => Utility.GetOrthoDistanceTurnCost(bot.X, bot.Y, bot.Tx, bot.Ty);
    }

    public bool NeedRadarOverride()
    {
        return  this.IdentifiedOreLow
            && !this.Bots.Values.Any(bot => bot.State == BotState.RADAR_RETRIEVE)
            && (this.RadarAssignments.Count <= 4 && (this.TurnCountToNextBotDeposit().turnCount > this.RadarCoolDown - 2))
            && !this.BaseRadarNeedsMet;
    }
    private bool HandleRadarOverride()
    {
        if(this.TurnCount < 2)
        {
            (int initialX, int initialY) = this.GetInitialRadarAssignmentLocation();
            Bot firstBot = this.Bots.Values.OrderBy(bot => Utility.GetOrthoDistanceTurnCost(bot.X, bot.Y, initialX, initialY)).FirstOrDefault();
            firstBot?.OverrideBotState(BotState.RADAR_RETRIEVE);
            return true;
        }

        // First pass check for bots that are not fetching
        foreach(Bot bot in this.GetBotsPerformingLowValueWork().OrderBy((bot) => bot.X))
        {
            if(bot.OverrideBotState(BotState.RADAR_RETRIEVE))
            {
                return true;
            }
        }

        int closestBot = -1;
        int closestBotDistanceToHq = int.MaxValue;

        foreach(Bot bot in this.Bots.Values.Where(bot => bot.ItemType != ItemType.TRAP && bot.ItemType != ItemType.RADAR && bot.State != BotState.DEAD))
        {
            int currentBotDistanceToHq = bot.X;
            if(currentBotDistanceToHq < closestBotDistanceToHq)
            {
                closestBot = bot.EntityId;
                closestBotDistanceToHq = currentBotDistanceToHq;
            }
        }

        if(closestBot != -1 && this.Bots[closestBot].OverrideBotState(BotState.RADAR_RETRIEVE))
        {
            return true;
        }

        return false;
    }

    private bool NeedEnemyRadarOverride() => this.PossibleEnemyRadarQueue.Count > 0 && !this.Bots.Values.Any(bot => bot.State == BotState.RADAR_SABOTAGE);

    private bool HandleEnemyRadarOverride()
    {
        // First pass check for bots that are not fetching
        foreach(Bot bot in this.GetBotsPerformingLowValueWork().OrderBy((bot) => bot.X))
        {
            if(bot.OverrideBotState(BotState.RADAR_SABOTAGE))
            {
                return true;
            }
        }

        int furthestBot = -1;
        int furthestBotDistanceToHq = -1;

        foreach(Bot bot in this.Bots.Values.Where(bot => bot.ItemType != ItemType.TRAP && bot.ItemType != ItemType.RADAR && bot.State != BotState.DEAD))
        {
            int distanceToHq = bot.X;
            if(distanceToHq > furthestBotDistanceToHq)
            {
                furthestBot = bot.EntityId;
                furthestBotDistanceToHq = distanceToHq;
            }
        }

        if(furthestBot != -1 && this.Bots[furthestBot].OverrideBotState(BotState.RADAR_SABOTAGE))
        {
            return true;
        }

        return false;
    }

    private bool ShouldSackABot()
    {
        bool botWillBeSacked = false;
        HashSet<(int X, int Y)> knownBombs = this.EnemyTrapRecords.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToHashSet();
        HashSet<(int X, int Y)> bombsConsideredInOtherBombAnalysis = new HashSet<(int X, int Y)>();

        foreach((int X, int Y) bombKey in knownBombs)
        {
            if(!bombsConsideredInOtherBombAnalysis.Contains(bombKey))
            {
                HashSet<int> enemyBotsInZone = new HashSet<int>();
                HashSet<int> alliedBotsInZone = new HashSet<int>();
                Bot sackBot = this.AnalyseBombImpact(bombKey, enemyBotsInZone, alliedBotsInZone, knownBombs, bombsConsideredInOtherBombAnalysis);

                if(sackBot != null)
                {
                    sackBot.Tx = bombKey.X;
                    sackBot.Ty = bombKey.Y;
                    sackBot.OverrideBotState(BotState.SACK);
                    botWillBeSacked = true;
                }
            }
        }
        return botWillBeSacked;
    }

    private Bot AnalyseBombImpact((int X, int Y) bombKey, HashSet<int> enemyBotsInZone, HashSet<int> alliedBotsInZone, HashSet<(int X, int Y)> knownBombs, HashSet<(int X, int Y)> bombsConsideredInOtherBombAnalysis)
    {
        foreach(EntityEnemyData enemyBot in this.EnemyTrackingData.Values.Where(b => !enemyBotsInZone.Contains(b.EntityId)))
        {
            if(Utility.GetOrthoDistance(bombKey.X, bombKey.Y, enemyBot.X, enemyBot.Y) <= 1)
            {
                enemyBotsInZone.Add(enemyBot.EntityId);
            }
        }


        foreach(Bot friendBot in this.Bots.Values.Where(b => b.State != BotState.DEAD && b.State != BotState.SACK && !alliedBotsInZone.Contains(b.EntityId)))
        {
            if(Utility.GetOrthoDistance(bombKey.X, bombKey.Y, friendBot.X, friendBot.Y) <= 1)
            {
                alliedBotsInZone.Add(friendBot.EntityId);
            }
        }

        (int X, int Y) adjacentKey = (bombKey.X + 1, bombKey.Y);
        if(knownBombs.Contains(adjacentKey) && !bombsConsideredInOtherBombAnalysis.Contains(adjacentKey))
        {
            bombsConsideredInOtherBombAnalysis.Add(adjacentKey);
            this.AnalyseBombImpact(bombKey, enemyBotsInZone, alliedBotsInZone, knownBombs, bombsConsideredInOtherBombAnalysis);
        }
        adjacentKey = (bombKey.X - 1, bombKey.Y);
        if(knownBombs.Contains(adjacentKey) && !bombsConsideredInOtherBombAnalysis.Contains(adjacentKey))
        {
            bombsConsideredInOtherBombAnalysis.Add(adjacentKey);
            this.AnalyseBombImpact(bombKey, enemyBotsInZone, alliedBotsInZone, knownBombs, bombsConsideredInOtherBombAnalysis);
        }
        adjacentKey = (bombKey.X, bombKey.Y + 1);
        if(knownBombs.Contains(adjacentKey) && !bombsConsideredInOtherBombAnalysis.Contains(adjacentKey))
        {
            bombsConsideredInOtherBombAnalysis.Add(adjacentKey);
            this.AnalyseBombImpact(bombKey, enemyBotsInZone, alliedBotsInZone, knownBombs, bombsConsideredInOtherBombAnalysis);
        }
        adjacentKey = (bombKey.X + 1, bombKey.Y - 1);
        if(knownBombs.Contains(adjacentKey) && !bombsConsideredInOtherBombAnalysis.Contains(adjacentKey))
        {
            bombsConsideredInOtherBombAnalysis.Add(adjacentKey);
            this.AnalyseBombImpact(bombKey, enemyBotsInZone, alliedBotsInZone, knownBombs, bombsConsideredInOtherBombAnalysis);
        }

        //if (alliedBotsInZone.Count > 0 || enemyBotsInZone.Count > 0)
        //{
        //    Console.Error.WriteLine($"Final Count for Sack at ({bombKey.X},{bombKey.Y}) is Allied: {alliedBotsInZone.Count} | Enemy: {enemyBotsInZone.Count}");
        //}

        if(alliedBotsInZone.Count > 0 && enemyBotsInZone.Count - alliedBotsInZone.Count > 1)
        {
            foreach(var bot in alliedBotsInZone)

                return this.Bots[alliedBotsInZone.FirstOrDefault()];
        }

        return null;
    }

    public bool ShouldTrap() => this.TrapCoolDown == 0 && this.TurnCount > 20 && this.TurnCount < 140 && this.Bots.Values.Where(b => b.State != BotState.DEAD).Count() > 2;

    private IEnumerable<Bot> GetBotsPerformingLowValueWork()
    {
        foreach(Bot bot in this.Bots.Values)
        {
            if(bot.BotDutyIsLowValue())
            {
                yield return bot;
            }
        }
    }

    #region Dispatch Reports

    public void HandleDispatchReportOre(int x, int y, OreStruct oreReport, OreDispatchReport dispatchReport)
    {
        if(this.OreData.TryGetValue((x, y), out OreStruct oreCurrent) && oreCurrent.HasBeenIdentified && dispatchReport != OreDispatchReport.ORE_FOUND_WAS_STALL)
        {
            // We don't do anything, because identified ore comes from radars and data will be accurately maintained without blind management.
        }
        else if(oreCurrent.HasBeenIdentified && dispatchReport == OreDispatchReport.ORE_FOUND_WAS_STALL)
        {
            oreReport.IsUsedForCache = true;
            this.CancelDigAssignmentForLocation((x, y));
            if(this.OreAssignments.TryGetValue((x, y), out OreAssignment ore)) ore.AssignedBots.Clear();
            this.OreData[(x, y)] = oreReport;
        }
        else
        {
            this.OreData[(x, y)] = oreReport;
        }
    }

    #endregion

}

public class Bot
{
    public Bot(EntityData entityData, BotOverSeer overSeer)
    {
        this.EntityId = entityData.EntityId;
        this.ItemType = entityData.ItemType;
        this.X = entityData.X;
        this.Y = entityData.Y;
        this.OverSeer = overSeer;
    }

    public int EntityId { get; set; }
    public ItemType ItemType { get; set; } = ItemType.NONE;
    public int X { get; set; }
    public int Y { get; set; }
    public BotState State { get; private set; } = BotState.IDLE;
    public BotSubState SubState { get; set; } = BotSubState.UNUSED;
    public int Tx { get; set; }
    public int Ty { get; set; }
    public int Ox { get; set; }
    public int Oy { get; set; }

    public BotOverSeer OverSeer { get; private set; }

    public void ReconcileEntityData(EntityData entityData)
    {
        this.ItemType = entityData.ItemType;
        this.X = entityData.X;
        this.Y = entityData.Y;
    }

    public void PrepareGoalForRound()
    {
        (this.State, this.SubState) = this.ProcessState();
    }

    public string PerformActForRound()
    {
        Console.Error.WriteLine($" Bot {this.EntityId} State: {this.State.ToString("G")} SubState: {this.SubState.ToString("G")} ({this.X},{this.Y}) => ({this.Tx},{this.Ty})");
        return this.ProcessOutput();
    }

    public bool BotDutyIsLowValue()
    {
        return this.State switch
        {
            BotState.IDLE => true,
            BotState.FETCHING => true,
            _ => false
        };
    }

    public bool OverrideBotState(BotState state, BotSubState subState = BotSubState.UNUSED)
    {
        //Console.Error.WriteLine($"Overriding Bot State of Bot {this.EntityId} from {this.State.ToString("G")} to {state.ToString("G")}");
        //Console.Error.WriteLine(Environment.StackTrace);
        this.State = state;
        this.SubState = subState;
        return true;
    }


    #region State Management
    private (BotState, BotSubState) ProcessState()
    {
        return this.State switch
        {
            BotState.IDLE => this.ProcessIdleState(),
            BotState.FETCHING => this.ProcessFetchingState(),
            BotState.DIGGING => this.ProcessDiggingState(),
            BotState.DEPOSITING => this.ProcessDepositingState(),
            BotState.RADAR_RETRIEVE => this.ProcessRadarRetrieveState(),
            BotState.RADAR_PLANT => this.ProcessRadarPlantState(),
            BotState.SCATTER => this.ProcessScatterState(),
            BotState.DEAD => (BotState.DEAD, BotSubState.UNUSED),
            BotState.TRAPPING_TRAVEL => this.ProcessTrappingTravelState(),
            BotState.TRAPPING => this.ProcessTrappingState(),
            BotState.RADAR_SABOTAGE => this.ProcessRadarSabotageState(),
            BotState.SACK => this.SubState == BotSubState.SHOULD_BE_DEAD ? this.ProcessIdleState() : (BotState.SACK, BotSubState.SHOULD_BE_DEAD),
            _ => this.ProcessIdleState()
        };
    }

    private (BotState, BotSubState) ProcessIdleState()
    {
        if(this.ItemType == ItemType.RADAR)
        {
            return this.ProcessRadarPlantState();
        }

        if(this.ItemType == ItemType.ORE)
        {
            return (BotState.DEPOSITING, BotSubState.UNUSED);
        }

        if(this.ItemType == ItemType.TRAP)
        {
            (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId, true);
            return this.ProcessTrappingTravelState();
        }

        if(this.X == 0 && this.OverSeer.ShouldWaitForRadar(this))
        {
            return (BotState.RADAR_RETRIEVE, BotSubState.UNUSED);
        }

        if(this.X == 0 && !this.OverSeer.NeedRadarOverride() && this.OverSeer.RadarCoolDown == 0 && !this.OverSeer.Bots.Values.Any(b => b.State == BotState.RADAR_RETRIEVE))
        {
            this.OverSeer.RadarCoolDown = 100;
            (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId, true);
            return (BotState.RADAR_RETRIEVE, BotSubState.RADAR_ASSIST);
        }

        (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId);
        if(Utility.GetOrthoDistance(this.Tx, this.Ty, this.X, this.Y) <= 1)
        {
            return (BotState.DIGGING, BotSubState.DIG_SHOULD_BE_OVER);
        }
        return (BotState.FETCHING, (this.X == 0 && (this.OverSeer.TurnCount < 2 || (this.OverSeer.OreData[(this.Tx, this.Ty)].OreCount > 1 && this.OverSeer.TurnCount < 65))) ? BotSubState.STALL : BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessFetchingState()
    {
        if(this.OverSeer.OreData.TryGetValue((this.X, this.Y), out OreStruct ore) && ore.HasBeenIdentified && ore.OreCount == 0)
        {
            (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId);
        }

        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            return (BotState.DIGGING, this.SubState == BotSubState.STALL || this.SubState == BotSubState.WAS_A_STALL ? BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER : BotSubState.DIG_SHOULD_BE_OVER);
        }
        return (BotState.FETCHING, this.SubState == BotSubState.STALL || this.SubState == BotSubState.WAS_A_STALL ? BotSubState.WAS_A_STALL : BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessDiggingState()
    {
        if(this.ItemType != ItemType.NONE)
        {
            if(this.ItemType == ItemType.ORE)
            {
                this.OverSeer.HandleDispatchReportOre(this.Tx, this.Ty, new OreStruct { HolePresent = true, OreCount = -1 },
                    this.SubState == BotSubState.WAS_A_STALL || this.SubState == BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER
                    ? OreDispatchReport.ORE_FOUND_WAS_STALL
                    : OreDispatchReport.ORE_FOUND);
            }
            return (BotState.DEPOSITING, this.SubState == BotSubState.WAS_A_STALL || this.SubState == BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER ? BotSubState.WAS_A_STALL : BotSubState.UNUSED);
        }

        if(this.SubState == BotSubState.DIG_SHOULD_BE_OVER || this.SubState == BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER)
        {
            this.OverSeer.HandleDispatchReportOre(this.Tx, this.Ty, new OreStruct { HolePresent = true, OreCount = 0 }, OreDispatchReport.NO_ORE_FOUND);
            return this.ProcessIdleState();
        }

        if(this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx, this.Ty)))
        {
            return this.ProcessIdleState();
        }

        return (BotState.DIGGING, this.SubState == BotSubState.WAS_A_STALL || this.SubState == BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER ? BotSubState.WAS_A_STALL_DIG_SHOULD_BE_OVER : BotSubState.DIG_SHOULD_BE_OVER);
    }

    private (BotState, BotSubState) ProcessDepositingState()
    {
        if(this.X == 0)
        {
            if(this.OverSeer.ShouldTrap())
            {
                return this.EnterTrappingState();
            }
            return this.ProcessIdleState();
        }

        return (BotState.DEPOSITING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) EnterTrappingState()
    {
        Random r = new Random();
        int offsetChoosen = r.Next(0, 3);
        if(offsetChoosen == 0)
        {
            this.Ox = -1;
            this.Oy = 0;
        }
        else if(offsetChoosen == 1)
        {
            this.Ox = 1;
            this.Oy = 0;
        }
        else if(offsetChoosen == 0)
        {
            this.Ox = 0;
            this.Oy = -1;
        }
        else
        {
            this.Ox = 0;
            this.Oy = 1;

        }

        (this.Tx, this.Ty) = this.OverSeer.GetTrapAssignment(this.EntityId);
        return (BotState.TRAPPING_TRAVEL, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessRadarRetrieveState()
    {
        if(this.ItemType == ItemType.RADAR)
        {
            if(this.SubState == BotSubState.RADAR_ASSIST)
            {
                return (BotState.FETCHING, BotSubState.UNUSED);
            }

            (this.Tx, this.Ty) = this.OverSeer.GetRadarAssignment(this.EntityId);
            return (BotState.RADAR_PLANT, BotSubState.UNUSED);
        }

        return (BotState.RADAR_RETRIEVE, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessRadarPlantState()
    {
        if(this.ItemType == ItemType.RADAR)
        {
            // Check if we are still safe to plant here
            if(this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx, this.Ty)))
            {

                if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx + 1, this.Ty + 0)))
                {
                    this.Tx++;
                }
                else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx + 0, this.Ty + 1)))
                {
                    this.Ty++;
                }
                else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx - 1, this.Ty + 0)))
                {
                    this.Tx--;
                }
                else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx + 0, this.Ty - 1)))
                {
                    this.Ty--;
                }
                else
                {
                    // We need to relocate at least one space
                    if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx + 1, this.Ty + 1)))
                    {
                        this.Tx++;
                        this.Ty++;
                    }
                    else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx - 1, this.Ty + 1)))
                    {
                        this.Tx--;
                        this.Ty++;
                    }
                    else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx - 1, this.Ty - 1)))
                    {
                        this.Tx--;
                        this.Ty--;
                    }
                    else if(!this.OverSeer.EnemyTrapRecords.ContainsKey((this.Tx + 1, this.Ty - 1)))
                    {
                        this.Tx++;
                        this.Ty--;
                    }
                }
            }

            return (BotState.RADAR_PLANT, BotSubState.UNUSED);
        }



        return this.ProcessIdleState();
    }

    private (BotState, BotSubState) ProcessScatterState()
    {
        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) < 1)
        {
            return this.ProcessIdleState();
        }

        return (BotState.SCATTER, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessTrappingTravelState()
    {
        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            return (BotState.TRAPPING, BotSubState.UNUSED);
        }

        return (BotState.TRAPPING_TRAVEL, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessTrappingState()
    {
        if(this.ItemType != ItemType.TRAP)
        {
            return this.ProcessIdleState();
        }

        return (BotState.TRAPPING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessRadarSabotageState()
    {
        if(this.SubState == BotSubState.RADAR_SABOTAGE_RELOCATE)
        {
            return (BotState.RADAR_SABOTAGE, Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) < 1 ? BotSubState.UNUSED : BotSubState.RADAR_SABOTAGE_RELOCATE);
        }

        if(this.SubState == BotSubState.UNUSED)
        {
            (int X, int Y) radKey = this.OverSeer.GetRadarSabotageAssignment(this.EntityId);
            if(radKey != default)
            {
                this.Tx = radKey.X;
                this.Ty = radKey.Y;

                return (BotState.RADAR_SABOTAGE, Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1 ? BotSubState.UNUSED : BotSubState.RADAR_SABOTAGE_RELOCATE);
            }

        }

        return this.ProcessIdleState();
    }

    #endregion

    #region Output Processing
    private string ProcessOutput()
    {
        return this.State switch
        {
            BotState.IDLE => this.ProcessIdleOutput(),
            BotState.FETCHING => this.ProcessFetchingOutput(),
            BotState.DIGGING => this.ProcessDiggingOutput(),
            BotState.DEPOSITING => this.ProcessDepositingOutput(),
            BotState.RADAR_RETRIEVE => this.ProcessRadarRetrieveOutput(),
            BotState.RADAR_PLANT => this.ProcessRadarPlantOutput(),
            BotState.DEAD => this.BuildWait(),
            BotState.SCATTER => this.ProcessScatterOutput(),
            BotState.TRAPPING_TRAVEL => this.ProcessTrappingTravelOutput(),
            BotState.TRAPPING => this.ProcessTrappingOutput(),
            BotState.RADAR_SABOTAGE => this.ProcessRadarSabotageOutput(),
            BotState.SACK => this.BuildDig(this.Tx, this.Ty),
            _ => this.ProcessIdleOutput()
        };
    }
    public string ProcessIdleOutput()
    {
        Console.Error.WriteLine($"Bot Is Idle at ({this.X},{this.Y}), this is probably not intentional");
        return this.BuildWait();
    }
    public string ProcessFetchingOutput()
    {
        if(this.SubState == BotSubState.STALL)
        {
            return this.BuildWait();
        }

        int destX = this.Tx;
        int destY = this.Ty;

        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) == 5)
        {
            if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx-1, this.Ty) == 4) destX--;
            else if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty-1) == 4) destY--;
            else if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty+1) == 4) destY++;
            else if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx + 1, this.Ty) == 4) destX++;
        }


        return this.BuildMove(this.X, this.Y, destX, destY);
    }
    public string ProcessDiggingOutput()
    {
        return this.BuildDig(this.Tx, this.Ty);
    }
    public string ProcessDepositingOutput()
    {
        if(this.X == 0)
        {
            this.BuildWait();
        }

        return this.BuildMove(this.X, this.Y, 0, this.Y);
    }
    public string ProcessRadarRetrieveOutput()
    {
        if(this.X == 0)
        {
            if(this.ItemType == ItemType.ORE)
            {
                this.BuildWait();
            }
            return this.BuildRequest(ItemType.RADAR);
        }

        return this.BuildMove(this.X, this.Y, 0, this.Y);
    }
    public string ProcessRadarPlantOutput()
    {
        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            return this.BuildDig(this.Tx, this.Ty);
        }

        return this.BuildMove(this.X, this.Y, this.Tx, this.Ty);
    }

    public string ProcessScatterOutput()
    {
        return this.BuildMove(this.X, this.Y, this.Tx, this.Ty);
    }

    public string ProcessTrappingTravelOutput()
    {

        if(this.ItemType != ItemType.TRAP)
        {
            if(this.X > 1)
            {
                Console.Error.WriteLine($"Bot at ({this.X},{this.Y}) is trapping when it probably shouldnt be");
            }
            return this.BuildRequest(ItemType.TRAP);
        }

        return this.BuildMove(this.X, this.Y, this.Tx + this.Ox, this.Ty + this.Oy);
    }

    public string ProcessTrappingOutput()
    {
        //Console.Error.WriteLine($"Logging Friendly Trap at ({this.Tx},{this.Ty})");
        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            this.OverSeer.ProcessFriendlyTrapEvent((this.Tx, this.Ty));
        }
        return this.BuildDig(this.Tx, this.Ty);
    }

    public string ProcessRadarSabotageOutput()
    {
        if(this.SubState == BotSubState.RADAR_SABOTAGE_RELOCATE)
        {
            //Console.Error.WriteLine($"Radar Sabotage Relocate for radar at ({this.Tx},{this.Ty})");
            return this.BuildMove(this.X, this.Y, this.Tx, this.Ty);
        }

        //Console.Error.WriteLine($"Radar Sabotage dig at ({this.Tx},{this.Ty})");
        return this.BuildDig(this.Tx, this.Ty);

    }

    #endregion
}


public static class Utility
{
    public static int GetOrthoDistance(int x, int y, int tx, int ty) => Math.Abs(tx - x) + Math.Abs(ty - y);

    public static int GetOrthoDistance(int x, int y, (int tx, int ty) k) => Math.Abs(k.tx - x) + Math.Abs(k.ty - y);

    public static int GetOrthoDistance((int x, int y) j, int tx, int ty) => Math.Abs(tx - j.x) + Math.Abs(ty - j.y);

    public static int GetOrthoDistance((int x, int y) j, (int tx, int ty) k) => Math.Abs(k.tx - j.x) + Math.Abs(k.ty - j.y);

    public static int GetOrthoDistanceTurnCost(int x, int y, int tx, int ty) => 1 + Utility.GetOrthoDistance(x, y, tx, ty) / 3;

    public static int GetOrthoDistanceTurnCost(int x, int y, (int tx, int ty) k) => 1 + Utility.GetOrthoDistance(x, y, k.tx, k.ty) / 3;

    public static int GetOrthoDistanceTurnCost((int x, int y) j, int tx, int ty) => 1 + Utility.GetOrthoDistance(j.x, j.y, tx, ty) / 3;

    public static int GetOrthoDistanceTurnCost((int x, int y) j, (int tx, int ty) k) => 1 + Utility.GetOrthoDistance(j.x, j.y, k.tx, k.ty) / 3;
    #region Command Building
    public static string BuildWait(this Bot bot)
    {
        bot.OverSeer.botActionsPriorRound.Enqueue(new BotActionData { Action = BotAct.WAIT, X = bot.X, Y = bot.Y });
        return "WAIT";
    }

    public static string BuildMove(this Bot bot, int x, int y, int tx, int ty)
    {
        bot.OverSeer.botActionsPriorRound.Enqueue(new BotActionData { Action = BotAct.MOVE, X = tx, Y = ty });

        if(tx > x && tx - x >= 4)
        {
            return $"MOVE {x + 4} {y}";
        }

        return $"MOVE {tx} {ty}";
    }

    public static string BuildDig(this Bot bot, int x, int y)
    {
        bot.OverSeer.botActionsPriorRound.Enqueue(new BotActionData { Action = BotAct.DIG, X = x, Y = y, Item = bot.ItemType });
        return $"DIG {x} {y}";
    }

    public static string BuildRequest(this Bot bot, ItemType itemType)
    {
        bot.OverSeer.botActionsPriorRound.Enqueue(new BotActionData { Action = BotAct.REQUEST, X = bot.X, Y = bot.Y, Item = itemType });
        return $"REQUEST {itemType.ToString("G")}";
    }
    #endregion

}

public struct OreStruct
{
    public int OreCount;
    public bool HolePresent;
    public bool HasBeenIdentified;
    public bool IsUsedForCache;
    public bool BombEnRoute;

    public void LogOreStruct(int x, int y)
    {
        if(this.OreCount > 0 && !this.HolePresent)
        {
            Console.Error.WriteLine($"Pos ({x}, {y}) | OreCount: {this.OreCount} | HolePresent: {this.HolePresent}");
        }
    }
}

public struct OreAssignment
{
    public HashSet<int> AssignedBots;
    public int AssignmentCount;
}

public struct RadarAssignment
{
    public int BotId;
}

public struct TrapAssignment
{
    public int BotId;
}

public struct EntityData
{
    public int EntityId;
    public EntityType EntityType;
    public int X;
    public int Y;
    public ItemType ItemType;

    public void LogEntity()
    {
        Console.Error.WriteLine($"EntityId: {this.EntityId} | EntityType: {this.EntityType.ToString("G")} | ({this.X}, {this.Y}) | {this.ItemType.ToString("G")}");
    }

}

public struct EntityEnemyData
{
    public EntityEnemyData(EntityData rawEntityData)
    {
        this.EntityId = rawEntityData.EntityId;
        this.EntityType = EntityType.BOT_ENEMY;
        this.X = rawEntityData.X;
        this.Y = rawEntityData.Y;
        this.ItemSuspect = ItemType.NONE;
        this.WaitCount = 0;
    }

    public EntityEnemyData(EntityData rawEntityData, ItemType itemType, int waitCount)
    {
        this.EntityId = rawEntityData.EntityId;
        this.EntityType = EntityType.BOT_ENEMY;
        this.X = rawEntityData.X;
        this.Y = rawEntityData.Y;
        this.ItemSuspect = itemType;
        this.WaitCount = waitCount;
    }

    public int EntityId;
    public EntityType EntityType;
    public int X;
    public int Y;
    public ItemType ItemSuspect;
    public int WaitCount;

    public void LogEnemyEntity()
    {
        Console.Error.WriteLine($"EntityId: {this.EntityId} | WaitCount: {this.WaitCount} | ({this.X}, {this.Y}) | ItemSuspect {this.ItemSuspect.ToString("G")}");
    }

}

public struct BotActionData
{
    public BotAct Action;
    public int X;
    public int Y;
    public ItemType Item;
}

public enum BotAct
{
    DIG,
    MOVE,
    WAIT,
    REQUEST
}


public enum BotState
{
    IDLE,
    FETCHING,
    DIGGING,
    DEPOSITING,
    RADAR_RETRIEVE,
    RADAR_PLANT,
    SCATTER,
    DEAD,
    TRAPPING_TRAVEL,
    TRAPPING,
    RADAR_SABOTAGE,
    SACK,
}

public enum BotSubState
{
    UNUSED,
    STALL,
    DIG_SHOULD_BE_OVER,
    RADAR_SABOTAGE_RELOCATE,
    RADAR_ASSIST,
    WAS_A_STALL,
    WAS_A_STALL_DIG_SHOULD_BE_OVER,
    SHOULD_BE_DEAD
}

public enum EntityType
{
    BOT_FRIEND = 0,
    BOT_ENEMY = 1,
    RADAR = 2,
    TRAP = 3
}

public enum ItemType
{

    NONE = -1,
    RADAR = 2,
    TRAP = 3,
    ORE = 4
}

public enum OreDispatchReport
{

    NO_ORE_FOUND,
    ORE_FOUND,
    ORE_FOUND_WAS_STALL
}