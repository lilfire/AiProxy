import type { Plugin, Config } from "@opencode-ai/plugin"
import http from "http"

interface OpenAiModel
{
    id: string
    object: string
    created: number
    owned_by: string
}

interface OpenAiModelsResponse
{
    object: string
    data: OpenAiModel[]
}

async function fetchModels(baseUrl: string): Promise<OpenAiModelsResponse>
{
    return new Promise((resolve, reject) =>
    {
        const url = new URL("/v1/models", baseUrl)
        const request = http.get(url, (response: http.IncomingMessage) =>
        {
            let data = ""

            response.on("data", (chunk: string) =>
            {
                data += chunk
            })

            response.on("end", () =>
            {
                try
                {
                    const parsed = JSON.parse(data) as OpenAiModelsResponse
                    resolve(parsed)
                }
                catch (error)
                {
                    reject(error)
                }
            })
        })

        request.on("error", (error: Error) => reject(error))
        request.setTimeout(5000, () =>
        {
            request.destroy()
            reject(new Error("Timeout ved henting av modeller"))
        })
    })
}

export default (async () =>
{
    return {
        config: async (cfg: Config): Promise<void> =>
        {
            const provider = cfg.provider?.["shell-ai"]
            if (!provider)
                return

            const baseUrl = provider.options?.baseURL as string | undefined
            if (!baseUrl)
                return

            try
            {
                const response = await fetchModels(baseUrl)
                const models = response.data ?? []

                provider.models = {}

                for (const model of models)
                {
                    provider.models[model.id] = {
                        id: model.id,
                        name: model.id
                    }
                }
            }
            catch (error)
            {
                console.error("Kunne ikke hente modeller fra AiProxy:", error)
            }
        },
        "chat.headers": async (input, output) =>
        {
            output.headers = output.headers || {}
            output.headers["X-ShellAi-Session"] = input.sessionID
        },
    }
}) satisfies Plugin
