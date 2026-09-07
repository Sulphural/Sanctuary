using System.Numerics;

using Sanctuary.Game.Entities;

namespace Sanctuary.Game.Quests;

public interface IQuestManager
{
    bool IsQuestNpc(ulong npcGuid);

    void OnNpcInteract(Player player, Npc npc);

    void OnCollectInteract(Player player, Npc npc);

    void OnPlayerMoved(Player player);

    void AcceptQuest(Player player, int questId);

    void CompleteQuest(Player player, int questId);

    void AbandonQuest(Player player, int questId);

    void SetActiveQuest(Player player, int questId);

    void RestoreJournal(Player player);

    void RefreshQuestNotification(Player player, ulong npcGuid);

    bool TryGetActiveObjectiveTarget(Player player, out Vector3 targetPosition);

    void RefreshObjectiveTarget(Player player);
}
