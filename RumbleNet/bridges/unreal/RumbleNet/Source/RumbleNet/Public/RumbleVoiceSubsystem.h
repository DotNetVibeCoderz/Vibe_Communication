// Rumble.Net Unreal bridge. Made by Gravicode Studios, led by Kang Fadhil.
#pragma once

#include "CoreMinimal.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "Tickable.h"
#include "RumbleVoiceSubsystem.generated.h"

struct RumbleClient;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FRumbleEventSignature, const FString&, Type, const FString&, Json);

UENUM(BlueprintType)
enum class ERumbleTransmitMode : uint8
{
    Continuous = 0,
    VoiceActivity = 1,
    PushToTalk = 2
};

/**
 * Game-instance wide Mumble voice client. Voice plays through the OS audio device (Rust core);
 * the listener pose is updated every tick from the first player's camera for positional audio.
 */
UCLASS()
class RUMBLENET_API URumbleVoiceSubsystem : public UGameInstanceSubsystem, public FTickableGameObject
{
    GENERATED_BODY()

public:
    virtual void Deinitialize() override;

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    bool Connect(const FString& Host, int32 Port, const FString& Username, const FString& Password, bool bPositional = true);

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    void Disconnect();

    UFUNCTION(BlueprintPure, Category = "Rumble|Voice")
    bool IsConnected() const;

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    void JoinChannel(int32 ChannelId);

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    void SendChannelMessage(int32 ChannelId, const FString& Message);

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    void SetTransmitMode(ERumbleTransmitMode Mode);

    UFUNCTION(BlueprintCallable, Category = "Rumble|Voice")
    void SetPushToTalk(bool bPressed);

    UFUNCTION(BlueprintPure, Category = "Rumble|Voice")
    float GetInputLevelDb() const;

    /** Raw native events (JSON). Broadcast on the game thread. */
    UPROPERTY(BlueprintAssignable, Category = "Rumble|Voice")
    FRumbleEventSignature OnRumbleEvent;

    // FTickableGameObject
    virtual void Tick(float DeltaTime) override;
    virtual bool IsTickable() const override { return Client != nullptr; }
    virtual ETickableTickType GetTickableTickType() const override { return ETickableTickType::Conditional; }
    virtual FStatId GetStatId() const override { RETURN_QUICK_DECLARE_CYCLE_STAT(URumbleVoiceSubsystem, STATGROUP_Tickables); }

private:
    static void OnNativeEvent(void* UserData, const uint8* Json, size_t Length);
    void SendCommand(const FString& Json);

    RumbleClient* Client = nullptr;
    bool bPositionalEnabled = false;
    TQueue<FString, EQueueMode::Mpsc> PendingEvents;
};
