using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Handlers.Abilities;

public interface IConsumableAbility
{
    bool Matches(ClientItemDefinition itemDefinition);

    bool Handle(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition);
}
