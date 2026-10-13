# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json evenup.slnx ./
COPY src/ src/
COPY examples/ examples/
RUN dotnet publish src/Evenup/Evenup.fsproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV EVENUP_HOST=0.0.0.0 \
    PORT=8080
EXPOSE 8080
USER app
HEALTHCHECK --interval=30s --timeout=3s CMD ["dotnet", "evenup.dll", "--version"]
ENTRYPOINT ["dotnet", "evenup.dll"]
CMD ["serve"]
