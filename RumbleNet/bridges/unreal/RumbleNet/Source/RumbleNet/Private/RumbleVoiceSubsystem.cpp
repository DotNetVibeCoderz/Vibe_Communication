// Rumble.Net Unreal bridge. Made by Gravicode Studios, led by Kang Fadhil.
#include "RumbleVoiceSubsystem.h"

#include "Camera/PlayerCameraManager.h"
#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

THIRD_PARTY_INCLUDES_START
#include "rumble.h"
THIRD_PARTY_INCLUDES_END

IMPLEMENT_MODULE(FDefaultModuleImpl, RumbleNet);

namespace
{
FString Escape(const FString& In)
{
    return In.Replace(TEXT("\\"), TEXT("\\\\")).Replace(TEXT("\""), TEXT("\\\""));
}

// Unreal: +X forward, +Y right, +Z up, centimeters. Mumble: +Z forward, +X right, +Y up, meters.
void ToMumble(const FVector& V, float* Out, bool bIsPosition)
{
    const float Scale = bIsPosition ? 0.01f : 1.0f;
    Out[0] = V.Y * Scale;
    Out[1] = V.Z * Scale;
    Out[2] = V.X * Scale;
}
}

void URumbleVoiceSubsystem::Deinitialize()
{
    Disconnect();
    Super::Deinitialize();
}

bool URumbleVoiceSubsystem::Connect(const FString& Host, int32 Port, const FString& Username, const FString& Password, bool bPositional)
{
    if (Client)
    {
        return true;
    }

    FString Json = FString::Printf(TEXT("{\"host\":\"%s\",\"port\":%d,\"username\":\"%s\",\"positionalTransmit\":%s%s}"),
        *Escape(Host), Port, *Escape(Username), bPositional ? TEXT("true") : TEXT("false"),
        Password.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(",\"password\":\"%s\""), *Escape(Password)));

    FTCHARToUTF8 Utf8(*Json);
    if (rumble_client_create(static_cast<const uint8_t*>(static_cast<void*>(const_cast<char*>(Utf8.Get()))), Utf8.Length(), &URumbleVoiceSubsystem::OnNativeEvent, this, &Client) != RUMBLE_OK)
    {
        Client = nullptr;
        return false;
    }

    bPositionalEnabled = bPositional;
    rumble_audio_set_positional(Client, bPositional ? 1 : 0, 1.0f, 25.0f, 0.1f, 0.25f);
    rumble_audio_set_transmit_mode(Client, RUMBLE_TRANSMIT_VOICE_ACTIVITY);
    return rumble_audio_set_mode(Client, RUMBLE_AUDIO_DEVICES, nullptr, 0, nullptr, 0) == RUMBLE_OK;
}

void URumbleVoiceSubsystem::Disconnect()
{
    if (Client)
    {
        // No callbacks are invoked after destroy returns, so `this` is safe afterwards.
        rumble_client_destroy(Client);
        Client = nullptr;
    }
}

bool URumbleVoiceSubsystem::IsConnected() const
{
    return Client && rumble_client_state(Client) == RUMBLE_STATE_CONNECTED;
}

void URumbleVoiceSubsystem::SendCommand(const FString& Json)
{
    if (!Client)
    {
        return;
    }

    FTCHARToUTF8 Utf8(*Json);
    rumble_client_send_command(Client, static_cast<const uint8_t*>(static_cast<void*>(const_cast<char*>(Utf8.Get()))), Utf8.Length());
}

void URumbleVoiceSubsystem::JoinChannel(int32 ChannelId)
{
    SendCommand(FString::Printf(TEXT("{\"type\":\"JoinChannel\",\"channelId\":%d}"), ChannelId));
}

void URumbleVoiceSubsystem::SendChannelMessage(int32 ChannelId, const FString& Message)
{
    SendCommand(FString::Printf(TEXT("{\"type\":\"SendTextMessage\",\"channelIds\":[%d],\"message\":\"%s\"}"), ChannelId, *Escape(Message)));
}

void URumbleVoiceSubsystem::SetTransmitMode(ERumbleTransmitMode Mode)
{
    if (Client)
    {
        rumble_audio_set_transmit_mode(Client, static_cast<int32_t>(Mode));
    }
}

void URumbleVoiceSubsystem::SetPushToTalk(bool bPressed)
{
    if (Client)
    {
        rumble_audio_set_push_to_talk(Client, bPressed ? 1 : 0);
    }
}

float URumbleVoiceSubsystem::GetInputLevelDb() const
{
    return Client ? rumble_audio_input_level(Client) : -96.0f;
}

void URumbleVoiceSubsystem::OnNativeEvent(void* UserData, const uint8* Json, size_t Length)
{
    auto* Self = static_cast<URumbleVoiceSubsystem*>(UserData);
    Self->PendingEvents.Enqueue(FString(static_cast<int32>(Length), UTF8_TO_TCHAR(reinterpret_cast<const char*>(Json))));
}

void URumbleVoiceSubsystem::Tick(float DeltaTime)
{
    FString Json;
    while (PendingEvents.Dequeue(Json))
    {
        FString Type;
        TSharedPtr<FJsonObject> Object;
        if (FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), Object) && Object.IsValid())
        {
            Object->TryGetStringField(TEXT("type"), Type);
        }
        OnRumbleEvent.Broadcast(Type, Json);
    }

    if (!Client || !bPositionalEnabled)
    {
        return;
    }

    const UWorld* World = GetGameInstance() ? GetGameInstance()->GetWorld() : nullptr;
    const APlayerController* PC = World ? World->GetFirstPlayerController() : nullptr;
    if (PC && PC->PlayerCameraManager)
    {
        const FRotator Rotation = PC->PlayerCameraManager->GetCameraRotation();
        float Pose[9];
        ToMumble(PC->PlayerCameraManager->GetCameraLocation(), Pose, true);
        ToMumble(Rotation.Vector(), Pose + 3, false);
        ToMumble(FRotationMatrix(Rotation).GetUnitAxis(EAxis::Z), Pose + 6, false);
        rumble_audio_set_listener(Client, Pose);
    }
}
