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
                            HolePresent = hole == 1
                        }))
                    {
                        oreData[(j, i)].LogOreStruct(i, j);
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

                entityData.LogEntity();
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
                }
                roundEntities[i] = entityData;
            }

            overSeer.ProcessOverrides();

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

    public int RadarCoolDown { get; set; }
    public int TrapCoolDown { get; set; }

    public int MapHeight { get; private set;}

    public int MapWidth { get; private set;}

    public BotOverSeer(int mapHeight, int mapWidth, Dictionary<int, Bot> bots, Dictionary<(int, int), OreStruct> oreData)
    {
        this.MapHeight = mapHeight;
        this.MapWidth = mapWidth;
        this.Bots = bots;
        this.OreData = oreData;
    }

    public (int X, int Y) GetOreAssignment(int entityId)
    {
        if(OreWasIdentifiedAsPresentQueue.TryDequeue(out (int X, int Y) idLoc))
        {
            this.OreAssignments[idLoc] = new OreAssignment { BotId = entityId };
            return idLoc;
        }

        if(OreCouldBePresentQueue.TryDequeue(out (int X, int Y) couldLoc))
        {
            this.OreAssignments[couldLoc] = new OreAssignment { BotId = entityId };
            return couldLoc;
        }

        foreach (KeyValuePair<(int X, int Y), OreStruct> oreLocation in OreData)
        {
            if (oreLocation.Value.OreCount != 0 && !this.OreAssignments.ContainsKey(oreLocation.Key))
            {
                this.OreAssignments[oreLocation.Key] = new OreAssignment { BotId = entityId };
                return oreLocation.Key;
            }
        }
        return (-1, -1);
    }

    public (int X, int Y) GetRadarAssignment(int entityId)
    {
        if(this.RadarAssignments.Count == 0)
        {
            this.RadarAssignments[( 1 + RADAR_RANGE / 2, 1 + RADAR_RANGE / 2)] = new RadarAssignment { BotId = entityId };
            return ( 1 + RADAR_RANGE / 2, 1 + RADAR_RANGE / 2);
        }

        KeyValuePair<(int X, int Y), RadarAssignment> lastAssignment = this.RadarAssignments.Last();

        if(lastAssignment.Key.X + RADAR_RANGE > this.MapWidth)
        {
            int tNewRowX = 1 + RADAR_RANGE / 2;
            int tNewRowY = lastAssignment.Key.Y + RADAR_RANGE;
            this.RadarAssignments[(tNewRowX, tNewRowY)] = new RadarAssignment { BotId= entityId };
            return (tNewRowX, tNewRowY);
        }

        int tNewColumnX = lastAssignment.Key.X + RADAR_RANGE;
        int tNewColumnY = lastAssignment.Key.Y ;
        this.RadarAssignments[(tNewColumnX, tNewColumnY)] = new RadarAssignment { BotId= entityId };
        return (tNewColumnX, tNewColumnY);
    }

    public void ProcessOverrides()
    {
        if(this.NeedRadarOverride())
        {
            _ = this.HandleRadarOverride();
        }
    }

    public bool ProcessRawOreStruct((int X, int Y) key, OreStruct oreStruct)
    {
        if(!this.OreData.ContainsKey(key))
        {
            this.OreData[key] = oreStruct;
            return true;
        }


        OreStruct currentOreStruct = this.OreData[key];

        // If we have a 0 we have confirmed there is no ore. Otherwise our data is potentially out of date
        if(currentOreStruct.OreCount != 0)
        {

            if(oreStruct.OreCount > 0)
            {
                this.OreWasIdentifiedAsPresentQueue.Enqueue(key); 
            }
            this.OreData[key] = oreStruct;
            return true;
        }

        return false;

        
    }
    private bool NeedRadarOverride()
    {
        return this.RadarCoolDown < 1;
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
        this.OreData[(x, y)] = oreReport;
        if(dispatchReport == OreDispatchReport.ORE_FOUND)
        {
            OreCouldBePresentQueue.Enqueue((x, y));
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
            _ => this.ProcessIdleState()
        };
    }

    private (BotState, BotSubState) ProcessIdleState()
    {
        (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId);
        this.SubState = BotSubState.UNUSED;
        return (BotState.FETCHING, BotSubState.UNUSED);
    }

    private (BotState, BotSubState) ProcessFetchingState()
    {
        if (this.X == this.Tx && this.Y == this.Ty)
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
                this.OverSeer.HandleDispatchReportOre(this.Tx, this.Ty, new OreStruct { HolePresent = true, OreCount = 0 }, OreDispatchReport.ORE_FOUND);
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

        return this.BuildMove(0, this.X);
    }
    public string ProcessRadarRetrieveOutput()
    {
        if(this.X == 0)
        {
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
    public int BotId;
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

public enum BotState
{
    IDLE,
    FETCHING,
    DIGGING,
    DEPOSITING,
    RADAR_RETRIEVE,
    RADAR_PLANT
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