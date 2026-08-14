FROM python:3.12-slim
WORKDIR /app
# requirements.txt carries an explicit benzene-results pin - see the comment in that file for
# why (a pre-release resolution bug in benzene-core's published metadata).
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app.py .
ENV PORT=8080
EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
