FROM nvidia/cuda:12.8.0-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    ffmpeg \
    git \
    git-lfs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN /opt/venv/bin/pip install --no-cache-dir fastapi "uvicorn[standard]"
RUN /opt/venv/bin/pip install --no-cache-dir torch torchaudio --index-url https://download.pytorch.org/whl/cu128

COPY requirements.txt /app/
RUN /opt/venv/bin/pip install --no-cache-dir -r requirements.txt

COPY inference_server.py /app/

EXPOSE 54321

CMD ["/opt/venv/bin/uvicorn", "inference_server:app", "--host", "0.0.0.0", "--port", "54321"]