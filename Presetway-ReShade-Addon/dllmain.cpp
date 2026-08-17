#include <reshade.hpp>
#include <atomic>
#include <mutex>
#include <string>
#include <optional>

#include "ThirdParty/Sharingway/Sharingway.h"

using namespace reshade::api;

namespace
{
    Sharingway::Subscriber* g_subscriber = nullptr;
    std::atomic<bool> g_subscribedToPresetway{ false };

    std::mutex g_queueMutex;
    std::optional<std::string> g_pendingPresetPath;
    std::string g_lastAppliedPresetPath;

    void on_sharingway_data(const std::string& provider, const json& data)
    {
        if (provider != "Presetway") return;
        if (!data.contains("presetPath") || !data["presetPath"].is_string()) return;

        const std::string presetPath = data["presetPath"].get<std::string>();

        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            if (presetPath == g_lastAppliedPresetPath) return;
            g_pendingPresetPath = presetPath;
        }

        reshade::log::message(reshade::log::level::info,
            ("Presetway: enqueued preset '" + presetPath + "'.").c_str());
    }

    void on_provider_status(const std::string& provider, Sharingway::ProviderStatus status)
    {
        if (provider != "Presetway") return;

        if (status == Sharingway::ProviderStatus::Online)
        {
            if (g_subscribedToPresetway.exchange(true)) return;

            reshade::log::message(reshade::log::level::info, "Presetway: provider online, subscribing.");
            if (g_subscriber != nullptr)
                g_subscriber->SubscribeTo(provider);
        }
        else
        {
            g_subscribedToPresetway.store(false);
        }
    }

    // Main‑thread callback – runs every frame, safely applies the queued preset.
    void on_reshade_present(effect_runtime* runtime)
    {
        if (runtime == nullptr) return;

        std::string toApply;
        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            if (!g_pendingPresetPath.has_value()) return;
            toApply = *g_pendingPresetPath;
            g_pendingPresetPath.reset();
        }

        reshade::log::message(reshade::log::level::info,
            ("Presetway: applying preset on main thread '" + toApply + "'.").c_str());

        runtime->set_current_preset_path(toApply.c_str());

        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            g_lastAppliedPresetPath = toApply;
        }
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD fdwReason, LPVOID)
{
    switch (fdwReason)
    {
    case DLL_PROCESS_ATTACH:
        if (!reshade::register_addon(hModule))
            return FALSE;

        // Use 'reshade_present' – this is the correct event for main‑thread per‑frame callbacks.
        reshade::register_event<reshade::addon_event::reshade_present>(&on_reshade_present);

        g_subscriber = new Sharingway::Subscriber();
        if (g_subscriber->Initialize())
        {
            g_subscriber->SetDataUpdateHandler(&on_sharingway_data);
            g_subscriber->SetProviderChangeHandler(&on_provider_status);

            for (const auto& providerInfo : g_subscriber->GetAvailableProviders())
            {
                if (providerInfo.name == "Presetway")
                {
                    g_subscriber->SubscribeTo(providerInfo.name);
                    g_subscribedToPresetway.store(true);
                }
            }

            reshade::log::message(reshade::log::level::info, "Presetway addon: Sharingway subscriber ready.");
        }
        else
        {
            reshade::log::message(reshade::log::level::warning,
                "Presetway addon: Sharingway subscriber failed to initialize.");
        }
        break;

    case DLL_PROCESS_DETACH:
        reshade::unregister_event<reshade::addon_event::reshade_present>(&on_reshade_present);

        delete g_subscriber;
        g_subscriber = nullptr;
        break;
    }

    return TRUE;
}
