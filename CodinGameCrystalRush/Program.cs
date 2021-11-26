using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;

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
        while (true)
        {
            inputs = Console.ReadLine().Split(' ');
            int myScore = int.Parse(inputs[0]); // Amount of ore delivered
            int opponentScore = int.Parse(inputs[1]);
            for (int i = 0; i < height; i++)
            {
                inputs = Console.ReadLine().Split(' ');
                for (int j = 0; j < width; j++)
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
            overSeer.TrapCoolDown  = int.Parse(inputs[2]); // turns left until a new trap can be requested
            
            EntityData[] roundEntities = new EntityData[entityCount];
            for (int i = 0; i < entityCount; i++)
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
                                
                switch (entityData.EntityType)
                {
                    case EntityType.BOT_FRIEND:
                        if (!bots.ContainsKey(entityData.EntityId))
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
                            entityData.LogEntity();
                        }
                        break;
                    case EntityType.TRAP: 
                        entityData.LogEntity();
                        break;
                    case EntityType.RADAR: 
                        //entityData.LogEntity();
                        break;
                }
                roundEntities[i] = entityData;
            }
            overSeer.ProcessOverrides(roundEntities);

            foreach (Bot bot in bots.Values)
            {
                Console.WriteLine(bot.PerformDuty());
            }
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

    private Queue<(int X, int Y)> OreCouldBePresentQueue = new Queue<(int X, int Y)>();
    private Queue<(int X, int Y)> OreWasIdentifiedAsPresentQueue = new Queue<(int X, int Y)>();
    
    public Dictionary<(int X, int Y), OreAssignment> OreAssignments { get; private set; } = new Dictionary<(int, int), OreAssignment>();
    public Dictionary<(int X, int Y), RadarAssignment> RadarAssignments { get; private set; } = new Dictionary<(int, int), RadarAssignment>();
    public Dictionary<(int X, int Y), bool> EnemyTrapRecords { get; private set;} = new Dictionary<(int, int), bool>();

    private Dictionary<int, EntityEnemyData> EnemyTrackingData { get; set; } = new Dictionary<int, EntityEnemyData>();

    public int RadarCoolDown { get; set; }
    public int TrapCoolDown { get; set; }

    private bool BaseRadarNeedsMet { get; set; } = false;
    private bool OffsetRadar { get; set; } = false;
    private int RoundsSinceQueueRebuild { get; set; } = 0;


    public int MapHeight { get; private set;}

    public int MapWidth { get; private set;}

    public BotOverSeer(int mapHeight, int mapWidth, Dictionary<int, Bot> bots, Dictionary<(int, int), OreStruct> oreData)
    {
        this.MapHeight = mapHeight;
        this.MapWidth = mapWidth;
        this.Bots = bots;
        this.OreData = oreData;
    }

    #region Assignment
    public (int X, int Y) GetOreAssignment(int entityId)
    {
        if(OreWasIdentifiedAsPresentQueue.TryDequeue(out (int X, int Y) idLoc))
        {
            return this.EnemyTrapRecords.ContainsKey(idLoc) 
                ? this.GetOreAssignment(entityId) 
                : this.ProcessOreAssignment(idLoc, entityId);
        }

        if (OreCouldBePresentQueue.TryDequeue(out (int X, int Y) couldLoc))
        {
            return this.EnemyTrapRecords.ContainsKey(couldLoc)
                ? this.GetOreAssignment(entityId)
                : this.ProcessOreAssignment(couldLoc, entityId); 
        }

        foreach (KeyValuePair<(int X, int Y), OreStruct> oreLocation in OreData)
        {
            if (oreLocation.Value.OreCount != 0 
            && !this.OreAssignments.ContainsKey(oreLocation.Key) 
            && !this.EnemyTrapRecords.ContainsKey(oreLocation.Key))
            {
                return this.ProcessOreAssignment(oreLocation.Key, entityId);
            }
        }
        return (-1, -1);
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
                AssignedBots = new HashSet<int>() { entityId } ,
                AssignmentCount = 1
            };
            
        }
        return key;
    }

    public (int X, int Y) GetRadarAssignment(int entityId)
    {
        if(this.RadarAssignments.Count == 0)
        {
            this.OffsetRadar = true;
            this.RadarAssignments[( 1 + RADAR_RANGE / 2, 1 + RADAR_RANGE / 2)] = new RadarAssignment { BotId = entityId };
            return ( 1 + RADAR_RANGE / 2, 1 + RADAR_RANGE / 2);
        }

        KeyValuePair<(int X, int Y), RadarAssignment> lastAssignment = this.RadarAssignments.Last();

        if(lastAssignment.Key.Y + (2* RADAR_RANGE) >= this.MapHeight)
        {
            if(lastAssignment.Key.X + RADAR_RANGE >= this.MapWidth)
            {
                this.Bots[entityId].OverrideBotState(BotState.IDLE);
                this.BaseRadarNeedsMet = true;
                return ( 1, this.Bots[entityId].Y);
            }

            if(this.OffsetRadar)
            {
                this.OffsetRadar = false;
                int tNewColumnXOffset = lastAssignment.Key.X + RADAR_RANGE;
                int tNewColumnYOffset = (1 + RADAR_RANGE / 2) + RADAR_RANGE;
                this.RadarAssignments[(tNewColumnXOffset, tNewColumnYOffset)] = new RadarAssignment { BotId= entityId };
                return (tNewColumnXOffset, tNewColumnYOffset);    
            }
            this.OffsetRadar = true;
            int tNewColumnX = lastAssignment.Key.X + RADAR_RANGE;
            int tNewColumnY = 1 + RADAR_RANGE / 2;
            this.RadarAssignments[(tNewColumnX, tNewColumnY)] = new RadarAssignment { BotId= entityId };
            return (tNewColumnX, tNewColumnY);
           
        }

        int tNewRowX = lastAssignment.Key.X;
        int tNewRowY = lastAssignment.Key.Y + (2 * RADAR_RANGE);
        this.RadarAssignments[(tNewRowX, tNewRowY)] = new RadarAssignment { BotId= entityId };
        return (tNewRowX, tNewRowY);
        
    }
    #endregion

    public void ProcessOverrides(EntityData[] entities)
    {
        this.ProcessFriendlyBotDeath(entities);

        if(this.NeedRadarOverride())
        {
            _ = this.HandleRadarOverride();
        }

        if(this.NeedOreIdQueueRebuilt())
        {
            this.RebuildOreIdentifiedQueue();
        }

        this.LogDiagnostics();
        
    }
    public bool ProcessRawOreStruct((int X, int Y) key, OreStruct oreStruct)
    {
        if(!this.OreData.ContainsKey(key))
        {
            this.OreData[key] = oreStruct;
            return true;
        }


        OreStruct currentOreStruct = this.OreData[key];

        // If we have a 0 and the input reports -1 we have identified this spot as empty without a radar
        if(currentOreStruct.OreCount != 0 && oreStruct.OreCount == -1)
        {
            return false;
        }

        if(oreStruct.OreCount > 0 && currentOreStruct.HasBeenIdentified == false)
        {
            oreStruct.HasBeenIdentified = true;
            this.OreData[key] = oreStruct;

            int outStandingOreDispatches = this.OreAssignments.TryGetValue(key, out OreAssignment storedOreAssignmentData) ? storedOreAssignmentData.AssignmentCount : 0;

            for (int i = 0; i < oreStruct.OreCount - outStandingOreDispatches; i++)
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
            this.EnemyTrackingData[entity.EntityId] = new EntityEnemyData(entity) { WaitCount = 0, ItemSuspect = ItemType.NONE };
            return true;
        }

        EntityEnemyData priorEnemyData = this.EnemyTrackingData[entity.EntityId];
        EntityEnemyData newEnemyData = new EntityEnemyData(entity) { WaitCount = priorEnemyData.WaitCount, ItemSuspect = ItemType.NONE };

        if(priorEnemyData.X == newEnemyData.X && priorEnemyData.Y == newEnemyData.Y)
        {
            if(++newEnemyData.WaitCount > 1 && newEnemyData.X == 0)
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
                this.ProcessTrapPlantEvent(newEnemyData);
                this.EnemyTrackingData[entity.EntityId] = newEnemyData;
                return true;    
            }

        }

        this.EnemyTrackingData[entity.EntityId] = newEnemyData;
        return false;
    }

    private void ProcessTrapPlantEvent(EntityData entityDataAtTimeOfTrapDrop)
    {
        // Because we can't identify where the trap was placed, we have to flag all adjacent areas.
        (int X, int Y) key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y);
        this.EnemyTrapRecords[key] 
            = this.EnemyTrapRecords.TryGetValue(key, out bool currentValue) && currentValue;
        this.CancelOreAssignmentForLocation(key);

        key = (entityDataAtTimeOfTrapDrop.X + 1, entityDataAtTimeOfTrapDrop.Y);
        this.EnemyTrapRecords[key] 
            = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
        this.CancelOreAssignmentForLocation(key);

        key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y + 1);
        this.EnemyTrapRecords[key] 
            = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
        this.CancelOreAssignmentForLocation(key);

        key = (entityDataAtTimeOfTrapDrop.X - 1, entityDataAtTimeOfTrapDrop.Y);
        this.EnemyTrapRecords[key] 
            = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;
        this.CancelOreAssignmentForLocation(key);

        key = (entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y - 1);
        this.EnemyTrapRecords[key] 
            = this.EnemyTrapRecords.TryGetValue(key, out currentValue) && currentValue;            
        this.CancelOreAssignmentForLocation(key);


        foreach(Bot bot in this.Bots.Values)
        {
            if(Utility.GetOrthoDistance(bot.X, bot.Y, entityDataAtTimeOfTrapDrop.X, entityDataAtTimeOfTrapDrop.Y) <= 2)
            {
                bot.OverrideBotState(BotState.SCATTER);
                bot.Tx = entityDataAtTimeOfTrapDrop.X > bot.X 
                    ? Math.Max(0, bot.X - 2)
                    : Math.Min(this.MapWidth, bot.X + 2);

                bot.Ty = entityDataAtTimeOfTrapDrop.Y > bot.Y 
                    ? Math.Max(0, bot.Y - 2)
                    : Math.Min(this.MapHeight, bot.Y + 2);    
            }
        }

    }

    private void ProcessFriendlyBotDeath(EntityData[] entities)
    {
        Dictionary<int, bool> aliveBots = new Dictionary<int, bool>();
        foreach(EntityData entity in entities)
        {
            if(entity.EntityType == EntityType.BOT_FRIEND)
            {
                aliveBots[entity.EntityId] = true;
            }
        }

        foreach(Bot bot in this.Bots.Values)
        {
            if(!aliveBots.ContainsKey(bot.EntityId))
            {
                bot.OverrideBotState(BotState.DEAD);
            }
        }
    }

    private void CancelOreAssignmentForLocation((int X, int Y) key)
    {
        if(this.OreAssignments.TryGetValue(key, out OreAssignment oreAssignment))
        {
            foreach( int botId in oreAssignment.AssignedBots)
            {
                Bot botToPotentiallyRecall = this.Bots[botId];
                if(botToPotentiallyRecall.Tx == key.X && botToPotentiallyRecall.Ty == key.Y)
                {
                    botToPotentiallyRecall.OverrideBotState(BotState.IDLE);
                }
            }
        }
    }

    private void LogDiagnostics()
    {
        Console.Error.Write("Trap Records");
        int printCount = 0;
        foreach((int X, int Y) key in this.EnemyTrapRecords.Keys)
        {
            if(++printCount > 5)
            {
                printCount = 0;
                Console.Error.WriteLine($"(   {key.X}, {key.Y})");
            }
            else
            {
                Console.Error.Write($"| ({key.X}, {key.Y})");
            }
        }
        Console.Error.WriteLine();

        Console.Error.Write("Ore Present Queue");
        printCount = 0;
        (int x, int y) priorKey = (-1, -1);
        for (int i = 0; i < this.OreCouldBePresentQueue.Count; i++)
        {
            (int x, int y) orePresentLocation = this.OreCouldBePresentQueue.Dequeue();
            if(orePresentLocation.x == priorKey.x && orePresentLocation.y == priorKey.y)
            {
                Console.Error.Write("+");
            }
            else
            {
                if(++printCount > 5)
                {
                    printCount = 0;
                    Console.Error.WriteLine($"(   {orePresentLocation.x}, {orePresentLocation.y})");
                }
                else
                {
                    Console.Error.Write($"| ({orePresentLocation.x}, {orePresentLocation.y})");
                }
            }
            priorKey = orePresentLocation;
            this.OreCouldBePresentQueue.Enqueue(orePresentLocation);
        }
        Console.Error.WriteLine();

        printCount = 0;
        priorKey = (-1, -1);
        Console.Error.Write("Ore Identified Queue | ");
        for (int i = 0; i < this.OreWasIdentifiedAsPresentQueue.Count; i++)
        {
            (int x, int y) oreIdentifiedLocation = this.OreWasIdentifiedAsPresentQueue.Dequeue();
            if(oreIdentifiedLocation.x == priorKey.x && oreIdentifiedLocation.y == priorKey.y)
            {
                Console.Error.Write("+");
            }
            else
            {
                if(++printCount > 5)
                {
                    printCount = 0;
                    Console.Error.WriteLine($"(   {oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
                }
                else
                {
                    Console.Error.Write($"| ({oreIdentifiedLocation.x}, {oreIdentifiedLocation.y})");
                }
            }
            priorKey = oreIdentifiedLocation;
            this.OreWasIdentifiedAsPresentQueue.Enqueue(oreIdentifiedLocation);
        }
        Console.Error.WriteLine();
    }
    
    private bool NeedOreIdQueueRebuilt() => ++this.RoundsSinceQueueRebuild > 10;
    private void RebuildOreIdentifiedQueue()
    {
        this.RoundsSinceQueueRebuild = 0;
        this.OreWasIdentifiedAsPresentQueue = new Queue<(int X, int Y)>();

        foreach(KeyValuePair<(int X, int Y), OreStruct> kvp in this.OreData.OrderBy(kvp => kvp.Key.X))
        {
            int potentialOreAssignment = kvp.Value.OreCount - (this.OreAssignments.TryGetValue(kvp.Key, out OreAssignment storedOreAssignmentData) ? storedOreAssignmentData.AssignmentCount : 0);
            if(potentialOreAssignment > 0)
            {
                for (int i = 0; i < potentialOreAssignment; i++)
                {
                    OreWasIdentifiedAsPresentQueue.Enqueue(kvp.Key);
                }
            }
            if(OreWasIdentifiedAsPresentQueue.Count > 20)
            {
                return;
            }
        }
    }
    private bool NeedRadarOverride()
    {
        return this.RadarCoolDown < 1 && !this.Bots.Values.Any(bot => bot.State == BotState.RADAR_RETRIEVE) && !this.BaseRadarNeedsMet;
    }
    private bool HandleRadarOverride()
    {
        // First pass check for bots that are not fetching
        foreach (Bot bot in this.GetBotsPerformingLowValueWork().OrderBy((bot) => bot.X))
        {
            if(bot.OverrideBotState(BotState.RADAR_RETRIEVE))
            {
                return true;
            }
        }

        int closestBot = -1;
        int closestBotDistanceToHq = int.MaxValue;

        foreach (Bot bot in this.Bots.Values.Where(bot => bot.ItemType != ItemType.TRAP && bot.ItemType != ItemType.RADAR && bot.State != BotState.DEAD))
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

    private IEnumerable<Bot> GetBotsPerformingLowValueWork()
    {
        foreach (Bot bot in this.Bots.Values)
        {
            if(!bot.BotDutyIsCurrentlyValuable())
            {
                yield return bot;
            }
        }
    }

    #region Dispatch Reports
    
    public void HandleDispatchReportOre(int x, int y, OreStruct oreReport, OreDispatchReport dispatchReport)
    {
        if(this.OreData[(x, y)].HasBeenIdentified)
        {
            // We don't do anything, because identified ore comes from radars and data will be accurately maintained without blind management.
        }
        else
        {
            this.OreData[(x, y)] = oreReport;
            if(dispatchReport == OreDispatchReport.ORE_FOUND)
            {
                OreStruct currentOreData = this.OreData[(x, y)];
                if(currentOreData.OreCount == -1)
                {
                    OreCouldBePresentQueue.Enqueue((x, y));
                }
            }
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

    public BotOverSeer OverSeer { get; private set; }

    public void ReconcileEntityData(EntityData entityData)
    {
        this.ItemType = entityData.ItemType;
        this.X = entityData.X;
        this.Y = entityData.Y;
    }

    public string PerformDuty()
    {
        (this.State, this.SubState) = this.ProcessState();
        return this.ProcessOutput();

    }

    public bool BotDutyIsCurrentlyValuable()
    {
        return this.State switch
        {
            BotState.DIGGING => true,
            BotState.DEPOSITING => true,
            BotState.RADAR_RETRIEVE => true,
            BotState.RADAR_PLANT => true,
            BotState.DEAD => true,
            _ => false
        };  
    }

    public bool OverrideBotState(BotState state, BotSubState subState = BotSubState.UNUSED)
    {
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
            _ => this.ProcessIdleState()
        };
    }

    private (BotState, BotSubState) ProcessIdleState()
    {
        if(this.ItemType == ItemType.RADAR)
        {
            return (BotState.RADAR_PLANT, BotSubState.UNUSED);
        }

        if(this.ItemType == ItemType.ORE)
        {
            return (BotState.DEPOSITING, BotSubState.UNUSED);
        }

        (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId);
        this.SubState = BotSubState.UNUSED;
        return (BotState.FETCHING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessFetchingState()
    {
        if (Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            return (BotState.DIGGING, BotSubState.UNUSED);
        }
        return (BotState.FETCHING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessDiggingState()
    {
        if (this.ItemType != ItemType.NONE)
        {
            if(this.ItemType == ItemType.ORE)
            {
                this.OverSeer.HandleDispatchReportOre(this.Tx, this.Ty, new OreStruct { HolePresent = true, OreCount = -1 }, OreDispatchReport.ORE_FOUND);
            }
            return (BotState.DEPOSITING, BotSubState.UNUSED);
        }

        if(this.SubState == BotSubState.DIG_SHOULD_BE_OVER)
        {
            this.OverSeer.HandleDispatchReportOre(this.Tx, this.Ty, new OreStruct { HolePresent = true, OreCount = 0 }, OreDispatchReport.NO_ORE_FOUND);
            return this.ProcessIdleState();
        }
        return (BotState.DIGGING, BotSubState.DIG_SHOULD_BE_OVER);
    }

    private (BotState, BotSubState) ProcessDepositingState()
    {
        if (this.ItemType == ItemType.NONE)
        {
            return this.ProcessIdleState();
        }

        return (BotState.DEPOSITING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessRadarRetrieveState()
    {
        if (this.ItemType == ItemType.RADAR)
        {
            (this.Tx, this.Ty) = this.OverSeer.GetRadarAssignment(this.EntityId);
            return (BotState.RADAR_PLANT, BotSubState.UNUSED);
        }

        return (BotState.RADAR_RETRIEVE, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessRadarPlantState()
    {
        if (this.ItemType == ItemType.RADAR)
        {
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
            _ => this.ProcessIdleOutput()
        };
    }
    public string ProcessIdleOutput()
    {
        Console.Error.WriteLine("Bot Is Idle, this is probably not intentional");
        return this.BuildWait();
    }
    public string ProcessFetchingOutput()
    {
        return this.BuildMove(this.Tx, this.Ty);
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

        return this.BuildMove(0, this.Y);
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

        return this.BuildMove(0, this.Y);
    }
    public string ProcessRadarPlantOutput()
    { 
        if(Utility.GetOrthoDistance(this.X, this.Y, this.Tx, this.Ty) <= 1)
        {
            return this.BuildDig(this.Tx, this.Ty);
        }

        return this.BuildMove(this.Tx, this.Ty);
    }

    public string ProcessScatterOutput()
    {
        return this.BuildMove(this.Tx, this.Ty);
    }
    #endregion
}


public static class Utility
{
    public static int GetOrthoDistance(int x, int y, int tx, int ty) => Math.Abs(tx -x) + Math.Abs(ty -y);

    #region Command Building
    public static string BuildWait(this Bot bot)
    {
        return "WAIT";
    }

    public static string BuildMove(this Bot bot, int x, int y)
    {
        return $"MOVE {x} {y}";
    }

    public static string BuildDig(this Bot bot, int x, int y)
    {
        return $"DIG {x} {y}";
    }

    public static string BuildRequest(this Bot bot, ItemType itemType)
    {
        return $"REQUEST {itemType.ToString("G")}";
    }
    #endregion

}

public struct OreStruct
{
    public int OreCount;
    public bool HolePresent;
    public bool HasBeenIdentified;

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

public ref struct TrapSurveilanceReport
{
    public int EntityId;
    public int X;
    public int Y;
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
    DEAD
}

public enum BotSubState
{
    UNUSED,
    DIG_SHOULD_BE_OVER
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
    ORE_FOUND
}