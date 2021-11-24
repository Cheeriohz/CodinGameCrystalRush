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
        BotOverSeer overSeer = new BotOverSeer(bots, oreData);

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

                    oreData[(i, j)] = new OreStruct
                    {
                        OreCount = ore == "?" ? -1 : int.TryParse(ore, out int oreAmount) ? oreAmount : -1,
                        HolePresent = hole == 1
                    };
                }
            }
            inputs = Console.ReadLine().Split(' ');
            int entityCount = int.Parse(inputs[0]); // number of entities visible to you
            int radarCooldown = int.Parse(inputs[1]); // turns left until a new radar can be requested
            int trapCooldown = int.Parse(inputs[2]); // turns left until a new trap can be requested
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
                        if (bots.ContainsKey(entityData.EntityId))
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

            foreach (Bot bot in bots.Values)
            {
                Console.WriteLine(bot.PerformDuty());
            }
        }
    }
}

public class BotOverSeer
{
    public Dictionary<int, Bot> Bots { get; private set; }
    public Dictionary<(int X, int Y), OreStruct> OreData { get; private set; }
    public Dictionary<(int X, int Y), OreAssignment> OreAssignments { get; private set; } = new Dictionary<(int, int), OreAssignment>();

    public BotOverSeer(Dictionary<int, Bot> bots, Dictionary<(int, int), OreStruct> oreData)
    {
        this.Bots = bots;
        this.OreData = oreData;
    }

    public (int X, int Y) GetOreAssignment(int entityId)
    {
        foreach (KeyValuePair<(int X, int Y), OreStruct> oreLocation in OreData)
        {
            if (!this.OreAssignments.ContainsKey(oreLocation.Key))
            {
                this.OreAssignments[oreLocation.Key] = new OreAssignment { BotId = entityId };
                return oreLocation.Key;
            }
        }
        return (-1, -1);
    }

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

    public BotState State { get; set; } = BotState.IDLE;
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
        this.State = this.ProcessState();
        return this.ProcessOutput();

    }

    #region State Management
    private BotState ProcessState()
    {
        return this.State switch
        {
            BotState.IDLE => this.ProcessIdleState(),
            BotState.FETCHING => this.ProcessFetchingState(),
            BotState.DIGGING => this.ProcessDiggingState(),
            BotState.DEPOSITING => this.ProcessDepositingState(),
            _ => this.ProcessIdleState()
        };
    }

    private BotState ProcessIdleState()
    {
        (this.Tx, this.Ty) = this.OverSeer.GetOreAssignment(this.EntityId);
        return BotState.FETCHING;
    }

    private BotState ProcessFetchingState()
    {
        if (this.X == this.Tx && this.Y == this.Ty)
        {
            return BotState.DIGGING;
        }
        return BotState.FETCHING;
    }

    private BotState ProcessDiggingState()
    {
        if (this.ItemType != ItemType.NONE)
        {
            return BotState.DEPOSITING;
        }
        return BotState.DIGGING;
    }

    private BotState ProcessDepositingState()
    {
        if (this.ItemType == ItemType.NONE)
        {
            return this.ProcessIdleState();
        }

        return BotState.DEPOSITING;
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



    #endregion
}


public static class Utility
{
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
        return $"REQUEST {(int)itemType}";
    }

}

public struct OreStruct
{
    public int OreCount;
    public bool HolePresent;
}

public struct OreAssignment
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

}

public enum BotState
{
    IDLE = 0,
    FETCHING = 1,
    DIGGING = 2,
    DEPOSITING = 3
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