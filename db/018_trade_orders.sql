-- Manual order journal (idempotent by client_order_id per user)
CREATE TABLE IF NOT EXISTS trade_orders (
    user_id           uuid        NOT NULL,
    exchange          text        NOT NULL,
    client_order_id   text        NOT NULL,
    exchange_order_id text,
    symbol            text        NOT NULL,
    side              text        NOT NULL,
    quantity          numeric     NOT NULL,
    reduce_only       boolean     NOT NULL DEFAULT false,
    status            text        NOT NULL,
    reject_reason     text,
    created_utc       timestamptz NOT NULL DEFAULT now(),
    updated_utc       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, client_order_id)
);
