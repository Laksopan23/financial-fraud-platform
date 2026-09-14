ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:9.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:9.0.20
FROM ${SDK_IMAGE} AS build
ARG PROJECT
WORKDIR /source
COPY . .
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release --no-restore -o /out /p:UseAppHost=false
RUN dotnet publish tools/FinancialFraudPlatform.HealthProbe/FinancialFraudPlatform.HealthProbe.csproj -c Release -o /health /p:UseAppHost=false
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out .
COPY --from=build /health /health
RUN mkdir -p /models /keys && chown -R $APP_UID /models /keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080 8081
ENTRYPOINT ["dotnet"]
