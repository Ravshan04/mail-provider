FROM python:3.14.8-slim-bookworm AS dependencies
WORKDIR /build
COPY requirements.lock ./
RUN pip install --no-cache-dir --require-hashes --prefix=/install -r requirements.lock

FROM python:3.14.8-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 MCP_HOST=0.0.0.0 MCP_PORT=8741
WORKDIR /app
COPY --from=dependencies /install /usr/local
COPY *.py ./
COPY skills ./skills
USER 1654:1654
EXPOSE 8741
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8741/health/live', timeout=2)"]
CMD ["python", "server.py"]
