using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PlayerUpdatePacketUpdatePositionHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PlayerUpdatePacketUpdatePositionHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!PlayerUpdatePacketUpdatePosition.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PlayerUpdatePacketUpdatePosition));
            return false;
        }

        // _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PlayerUpdatePacketUpdatePosition), packet);

        connection.Player.Mount?.UpdatePosition(packet.Position, packet.Rotation);

        // Update pet position to follow owner
        if (connection.Player.Pet is not null)
        {
            var pet = connection.Player.Pet;

            // Check if owner is moving
            var ownerMoveDx = packet.Position.X - pet.OwnerLastPosition.X;
            var ownerMoveDz = packet.Position.Z - pet.OwnerLastPosition.Z;
            var ownerMoveDistance = System.MathF.Sqrt(ownerMoveDx * ownerMoveDx + ownerMoveDz * ownerMoveDz);
            const float ownerMovementThreshold = 0.05f; // Consider owner stationary if moved less than this
            bool ownerIsMoving = ownerMoveDistance > ownerMovementThreshold;

            // Update owner's last position for next iteration
            pet.OwnerLastPosition = packet.Position;

            // Calculate forward direction from player rotation
            var rotation = packet.Rotation;
            var forward = new System.Numerics.Vector3(
                2.0f * (rotation.X * rotation.Z + rotation.W * rotation.Y),
                2.0f * (rotation.Y * rotation.Z - rotation.W * rotation.X),
                1.0f - 2.0f * (rotation.X * rotation.X + rotation.Y * rotation.Y)
            );

            // Target position: 3 units behind and slightly to the side of the player
            const float followDistance = 3.0f;
            var targetPetPosition = new System.Numerics.Vector4(
                packet.Position.X - forward.X * followDistance,
                packet.Position.Y,
                packet.Position.Z - forward.Z * followDistance,
                packet.Position.W
            );

            var currentPetPosition = pet.Position;

            // Calculate distance to target
            var dx = targetPetPosition.X - currentPetPosition.X;
            var dz = targetPetPosition.Z - currentPetPosition.Z;
            var distance = System.MathF.Sqrt(dx * dx + dz * dz);

            // Calculate distance from pet to owner (not target position)
            var ownerDx = packet.Position.X - currentPetPosition.X;
            var ownerDz = packet.Position.Z - currentPetPosition.Z;
            var distanceToOwner = System.MathF.Sqrt(ownerDx * ownerDx + ownerDz * ownerDz);

            // Movement thresholds
            const float teleportDistance = 20.0f;  // Teleport if too far
            const float stopDistance = 1.5f;       // Stop if close enough to target
            const float runDistance = 8.0f;        // Run if farther than this
            const float idleRange = 6.0f;          // Stay idle if within this range of owner when they're not moving

            // Movement speeds
            const float walkSpeed = 4.5f;          // Walk speed
            const float runSpeed = 9.0f;           // Run speed when catching up
            const float smoothingFactor = 0.15f;   // Lower = smoother (0.1-0.3 works well)

            System.Numerics.Vector4 newPetPosition;
            byte movementState;

            if (distance > teleportDistance)
            {
                // Pet is too far - teleport to owner
                newPetPosition = targetPetPosition;
                movementState = 0; // Idle state after teleport

                _logger.LogDebug("Pet teleported to owner. Distance was {distance}", distance);
            }
            else if (!ownerIsMoving && distanceToOwner < idleRange)
            {
                // Owner is stationary and pet is close enough - stay idle
                newPetPosition = currentPetPosition;
                movementState = 0; // Idle state
            }
            else if (distance < stopDistance)
            {
                // Pet is close enough to target position - stop moving
                newPetPosition = currentPetPosition;
                movementState = 0; // Idle state
            }
            else
            {
                // Pet is at a reasonable distance - move smoothly
                var speed = distance > runDistance ? runSpeed : walkSpeed;
                movementState = distance > runDistance ? (byte)2 : (byte)1; // 2 = running, 1 = walking

                // Smooth interpolation towards target
                newPetPosition = new System.Numerics.Vector4(
                    currentPetPosition.X + dx * smoothingFactor * (speed / walkSpeed),
                    targetPetPosition.Y, // Match player Y position
                    currentPetPosition.Z + dz * smoothingFactor * (speed / walkSpeed),
                    currentPetPosition.W
                );
            }

            // Calculate rotation to face movement direction
            var petRotation = rotation;
            if (movementState > 0 && distance > 0.1f)
            {
                // Make pet face the direction it's moving
                var angle = System.MathF.Atan2(dx, dz);
                var halfAngle = angle / 2.0f;
                petRotation = new System.Numerics.Quaternion(
                    0,
                    System.MathF.Sin(halfAngle),
                    0,
                    System.MathF.Cos(halfAngle)
                );
            }

            pet.UpdatePosition(newPetPosition, petRotation);

            // Send ExpectedSpeed packet when movement state changes
            if (movementState != pet.Animation)
            {
                var newSpeed = movementState switch
                {
                    0 => 0f,        // Idle - no movement
                    1 => walkSpeed, // Walking
                    2 => runSpeed,  // Running
                    _ => walkSpeed
                };

                pet.Speed = newSpeed;

                var speedPacket = new PlayerUpdatePacketExpectedSpeed
                {
                    Guid = pet.Guid,
                    ExpectedSpeed = newSpeed
                };

                connection.Player.SendTunneledToVisible(speedPacket, true);
            }

            // Send position updates only if pet moved significantly
            var lastSentPos = pet.LastSentPosition;
            var sendDx = newPetPosition.X - lastSentPos.X;
            var sendDz = newPetPosition.Z - lastSentPos.Z;
            var sendDistance = System.MathF.Sqrt(sendDx * sendDx + sendDz * sendDz);

            const float minSendDistance = 0.1f; // Reduced from 0.2f for smoother updates
            if (sendDistance >= minSendDistance || movementState != pet.Animation)
            {
                var petUpdate = new PlayerUpdatePacketUpdatePosition
                {
                    Guid = pet.Guid,
                    Position = newPetPosition,
                    Rotation = petRotation,
                    State = movementState,
                    Unknown = packet.Unknown
                };

                pet.LastSentPosition = newPetPosition;
                pet.Animation = movementState; // Track current animation state
                connection.Player.SendTunneledToVisible(petUpdate, true);
            }
        }

        connection.Player.UpdatePosition(packet.Position, packet.Rotation);

        connection.Player.SendTunneledToVisible(packet);

        return true;
    }
}