﻿using Stratis.SmartContracts;
using System;
using System.Text;

/// <summary>
/// A Stratis smart contract for running a game battle where owner will start the battle and maximum 4 users can enter a battle
/// </summary>
public class Arena : SmartContract
{
    private const uint MaxUsers = 4;
    public struct BattleMain
    {
        public ulong BattleId;
        public Address Winner;
        public Address[] Users;
        public ulong Fee;
        public bool IsCancelled;
    }
    public struct BattleUser
    {
        public uint Score;
        public bool ScoreSubmitted;
    }
    public struct BattleHighestScorer
    {
        public uint Score;
        public Address[] Scorers;
    }
    public struct ClaimPendingDeployerOwnershipLog
    {
        [Index] public Address From;
        [Index] public Address To;
    }
    public struct SetPendingDeployerOwnershipLog
    {
        [Index] public Address From;
        [Index] public Address To;
    }
    public struct BattleEventLog
    {
        [Index] public string Event;
        [Index] public ulong BattleId;
        [Index] public Address Address;
    }
    /// <summary>
    /// Set the address deploying the contract as battle owner
    /// </summary>
    public Address Owner
    {
        get => State.GetAddress(nameof(Owner));
        private set => State.SetAddress(nameof(Owner), value);
    }
    public Address PendingOwner
    {
        get => State.GetAddress(nameof(PendingOwner));
        private set => State.SetAddress(nameof(PendingOwner), value);
    }
    /// <summary>
    /// Set the unique battleId of each battle
    /// </summary>
    public ulong NextBattleId
    {
        get => State.GetUInt64(nameof(NextBattleId));
        private set => State.SetUInt64(nameof(NextBattleId), value);
    }

    public Arena(ISmartContractState smartContractState) : base(smartContractState)
    {
        Owner = Message.Sender;
        NextBattleId = 1;
    }
    public void SetPendingOwnership(Address pendingOwner)
    {
        EnsureOwnerOnly();

        PendingOwner = pendingOwner;

        Log(new SetPendingDeployerOwnershipLog { From = Message.Sender, To = pendingOwner });
    }
    public void ClaimPendingOwnership()
    {
        var pendingOwner = PendingOwner;

        Assert(Message.Sender == pendingOwner, "HASHBATTLE: UNAUTHORIZED");

        var oldOwner = Owner;

        Owner = pendingOwner;
        PendingOwner = Address.Zero;

        Log(new ClaimPendingDeployerOwnershipLog { From = oldOwner, To = pendingOwner });
    }
    /// <summary>
    /// Battle owner will start the battle
    /// </summary>
    public ulong StartBattle(ulong fee)
    {
        Assert(Message.Sender == Owner, "Only battle owner can start game.");
        Assert(fee < ulong.MaxValue / MaxUsers, "Fee is too high");

        var battleId = NextBattleId;
        NextBattleId += 1;

        var battle = new BattleMain
        {
            BattleId = battleId,
            Fee = fee,
            Users = new Address[MaxUsers]
        };
        SetBattle(battleId, battle);

        Log(new BattleEventLog { Event = "Start", BattleId = battleId, Address = Message.Sender });
        return battleId;
    }
    /// <summary>
    /// 4 different user will enter the battle
    /// </summary>
    public void EnterBattle(ulong battleId)
    {
        var battle = GetBattle(battleId);

        Assert(battle.Winner == Address.Zero, "Battle not found.");

        Assert(battle.Fee == Message.Value, "Battle fee is not matching with entry fee paid.");

        var user = GetUser(battleId, Message.Sender);

        Assert(!user.ScoreSubmitted, "The user already submitted score.");

        SetUser(battleId, Message.Sender, user);

        var userindex = GetUserIndex(battleId);
        Assert(userindex != MaxUsers, "Max user reached for this battle.");
        battle.Users.SetValue(Message.Sender, userindex);
        SetUserIndex(battleId, (userindex + 1));

        SetBattle(battleId, battle);

        Log(new BattleEventLog { Event = "Enter", BattleId = battleId, Address = Message.Sender });
    }
    /// <summary>
    /// 4 different user will end the battle and submit the score
    /// </summary>
    public void EndBattle(Address userAddress, ulong battleId, uint score)
    {
        Assert(Message.Sender == Owner, "Only battle owner can end game.");

        var battle = GetBattle(battleId);

        Assert(battle.Winner == Address.Zero, "Battle not found.");

        var user = GetUser(battleId, userAddress);

        Assert(!user.ScoreSubmitted, "The user already submitted score.");

        user.Score = score;
        user.ScoreSubmitted = true;

        SetUser(battleId, userAddress, user);

        var ScoreSubmittedCount = GetScoreSubmittedCount(battleId);
        ScoreSubmittedCount += 1;
        SetScoreSubmittedCount(battleId, ScoreSubmittedCount);

        var highestScorer = GetHighestScorer(battleId);

        if (score > highestScorer.Score)
            SetHighestScorer(battleId, new BattleHighestScorer { Scorers = new Address[] { userAddress }, Score = score });
        else if (score == highestScorer.Score)
        {
            var scorers = highestScorer.Scorers;
            Array.Resize(ref scorers, scorers.Length + 1);
            scorers[scorers.Length - 1] = userAddress;
            SetHighestScorer(battleId, new BattleHighestScorer { Scorers = scorers, Score = score });
        }

        if (ScoreSubmittedCount == MaxUsers)
        {
            highestScorer = GetHighestScorer(battleId);
            if (highestScorer.Scorers.Length > 1)
                CancelBattle(battle);
            else
                ProcessWinner(battle, highestScorer.Scorers[0]);
        }

        Log(new BattleEventLog { Event = "End", BattleId = battleId, Address = Message.Sender });
    }
    /// <summary>
    /// Get winner address
    /// </summary>
    public Address GetWinner(ulong battleId)
    {
        var battle = GetBattle(battleId);
        return battle.Winner;
    }
    /// <summary>
    /// Process winner when all user scores are submitted
    /// </summary>
    private void ProcessWinner(BattleMain battle, Address winnerAddress)
    {
        battle.Winner = winnerAddress;
        SetBattle(battle.BattleId, battle);
        ProcessPrize(battle);
    }
    /// <summary>
    /// Send 3/4 amount to winner and 1/4 amount to battle owner
    /// </summary>
    private void ProcessPrize(BattleMain battle)
    {
        var prize = battle.Fee * (MaxUsers - 1);
        Transfer(battle.Winner, prize);
        Transfer(Owner, battle.Fee);
    }
    /// <summary>
    /// Cancel battle and refund the fee amount
    /// </summary>
    private void CancelBattle(BattleMain battle)
    {
        battle.IsCancelled = true;
        SetBattle(battle.BattleId, battle);

        Transfer(battle.Users[0], battle.Fee);
        Transfer(battle.Users[1], battle.Fee);
        Transfer(battle.Users[2], battle.Fee);
        Transfer(battle.Users[3], battle.Fee);
    }
    private void SetBattle(ulong battleId, BattleMain battle)
    {
        State.SetStruct($"battle:{battleId}", battle);
    }
    public BattleMain GetBattle(ulong battleId)
    {
        return State.GetStruct<BattleMain>($"battle:{battleId}");
    }
    private void SetUser(ulong battleId, Address address, BattleUser user)
    {
        State.SetStruct($"user:{battleId}-{address}", user);
    }
    public BattleUser GetUser(ulong battleId, Address address)
    {
        return State.GetStruct<BattleUser>($"user:{battleId}-{address}");
    }
    private void SetHighestScorer(ulong battleId, BattleHighestScorer highestScorer)
    {
        State.SetStruct($"scorer:{battleId}", highestScorer);
    }
    public BattleHighestScorer GetHighestScorer(ulong battleId)
    {
        return State.GetStruct<BattleHighestScorer>($"scorer:{battleId}");
    }
    private void SetUserIndex(ulong battleId, uint userindex)
    {
        State.SetUInt32($"user:{battleId}", userindex);
    }
    private uint GetUserIndex(ulong battleId)
    {
        return State.GetUInt32($"user:{battleId}");
    }
    private void SetScoreSubmittedCount(ulong battleId, uint scoresubmitcount)
    {
        State.SetUInt32($"scoresubmit:{battleId}", scoresubmitcount);
    }
    private uint GetScoreSubmittedCount(ulong battleId)
    {
        return State.GetUInt32($"scoresubmit:{battleId}");
    }
    private void EnsureOwnerOnly()
    {
        Assert(Message.Sender == Owner, "HASHBATTLE: UNAUTHORIZED");
    }
}